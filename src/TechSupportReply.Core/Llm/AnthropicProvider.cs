using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Llm
{
    /// <summary>
    /// 공식 Anthropic C# SDK 기반 공급자. 적응형 사고 + effort, 시스템 프롬프트 캐싱,
    /// 구조화 출력, 스트리밍, refusal 폴백(claude-opus-4-8)을 사용한다.
    /// </summary>
    public sealed class AnthropicProvider : ILlmProvider
    {
        private static readonly string[] UnsupportedModelPrefixes =
        {
            "claude-3", "claude-haiku-4-5", "claude-sonnet-4-5", "claude-opus-4-5", "claude-opus-4-1",
            "claude-opus-4-0", "claude-sonnet-4-0", "claude-opus-4-2025", "claude-sonnet-4-2025",
        };

        private readonly LlmProfile _profile;
        private readonly AnthropicClient _client;

        public AnthropicProvider(LlmProfile profile, string apiKey)
            : this(profile, apiKey, null)
        {
        }

        /// <summary>handler는 테스트용이다. null이고 WorkspaceId도 없으면 SDK 기본 HttpClient를 쓴다.</summary>
        internal AnthropicProvider(LlmProfile profile, string apiKey, HttpMessageHandler handler)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            EnsureSupportedModel(profile.Model);
            if (string.IsNullOrWhiteSpace(apiKey)) throw new LlmException(LlmErrorKind.Authentication, "API 키가 비어 있습니다.");
            var workspaceId = (profile.WorkspaceId ?? "").Trim();
            _client = workspaceId.Length == 0 && handler == null
                ? new AnthropicClient
                {
                    ApiKey = apiKey,
                    Handlers = [new BetaRefusalFallbackHandler { Fallbacks = [new(Model.ClaudeOpus4_8)] }],
                }
                : new AnthropicClient
                {
                    ApiKey = apiKey,
                    Handlers = [new BetaRefusalFallbackHandler { Fallbacks = [new(Model.ClaudeOpus4_8)] }],
                    HttpClient = CreateHttpClient(workspaceId, handler),
                };
        }

        internal static HttpClient CreateHttpClient(string workspaceId, HttpMessageHandler handler)
        {
            var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            // 요청별 시간 제한은 SDK의 Timeout 설정이 맡는다. HttpClient 기본 100초는 긴 스트리밍을 끊는다.
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            if (!string.IsNullOrEmpty(workspaceId)) http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-workspace-id", workspaceId);
            return http;
        }

        public string DisplayName => _profile.DisplayName;

        /// <summary>이 앱이 쓸 수 있는 모델인지(적응형 사고를 지원하는 Claude 4.6 이상). 모델 목록 필터에도 쓴다.</summary>
        public static bool IsSupportedModel(string model) =>
            !string.IsNullOrWhiteSpace(model) && !UnsupportedModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        public static void EnsureSupportedModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("모델명이 비어 있습니다.");
            if (!IsSupportedModel(model))
                throw new ArgumentException(
                    $"'{model}'은(는) 지원하지 않습니다. 적응형 사고를 지원하는 Claude 4.6 이상 모델(예: claude-opus-5, claude-sonnet-5)을 사용하세요.");
        }

        public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var parameters = BuildParams(request);
            try
            {
                var response = await _client.Messages.Create(parameters, ct).ConfigureAwait(false);
                if (response.StopReason == "refusal") throw new LlmException(LlmErrorKind.Refusal, "stop_reason=refusal");
                return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            }
            catch (Exception ex) when (Translate(ex) is LlmException mapped)
            {
                throw mapped;
            }
        }

        public async Task<string> StreamAsync(LlmRequest request, Action<string> onDelta, CancellationToken ct)
        {
            var parameters = BuildParams(request);
            var text = new StringBuilder();
            bool refused = false;
            try
            {
                await foreach (RawMessageStreamEvent ev in _client.Messages.CreateStreaming(parameters, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var piece))
                    {
                        text.Append(piece.Text);
                        onDelta?.Invoke(piece.Text);
                    }
                    else if (ev.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason == "refusal")
                    {
                        refused = true;
                    }
                }
            }
            catch (Exception ex) when (Translate(ex) is LlmException mapped)
            {
                throw mapped;
            }
            if (refused) throw new LlmException(LlmErrorKind.Refusal, "stop_reason=refusal");
            return text.ToString();
        }

        internal MessageCreateParams BuildParams(LlmRequest r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (string.IsNullOrWhiteSpace(r.CachedSystem)) throw new ArgumentException("CachedSystem은 비어 있을 수 없습니다.");

            var system = new List<TextBlockParam>
            {
                new TextBlockParam { Text = r.CachedSystem, CacheControl = new CacheControlEphemeral() },
            };
            if (!string.IsNullOrWhiteSpace(r.System)) system.Add(new TextBlockParam { Text = r.System });

            var messages = r.Messages
                .Select(m => new MessageParam { Role = m.Role == LlmRole.User ? Role.User : Role.Assistant, Content = m.Content })
                .ToList();

            var effort = ToEffort(r.Effort ?? _profile.Effort);
            var outputConfig = string.IsNullOrEmpty(r.JsonSchema)
                ? new OutputConfig { Effort = effort }
                : new OutputConfig { Effort = effort, Format = new JsonOutputFormat { Schema = ParseSchema(r.JsonSchema) } };

            return new MessageCreateParams
            {
                Model = _profile.Model,
                MaxTokens = r.MaxTokens > 0 ? r.MaxTokens : _profile.MaxTokens,
                System = system,
                Messages = messages,
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = outputConfig,
            };
        }

        internal static Effort ToEffort(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "low": return Effort.Low;
                case "high": return Effort.High;
                case "max": return Effort.Max;
                default: return Effort.Medium;
            }
        }

        private static Dictionary<string, JsonElement> ParseSchema(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            }
        }

        internal static LlmException Translate(Exception ex)
        {
            if (!(ex is LlmException) && !(ex is OperationCanceledException)
                && (ex.Message ?? "").IndexOf("anthropic-workspace-id", StringComparison.OrdinalIgnoreCase) >= 0)
                return new LlmException(LlmErrorKind.WorkspaceRequired, ex.Message, ex);
            switch (ex)
            {
                case LlmException _:
                case OperationCanceledException _:
                    return null;
                case AnthropicUnauthorizedException e: return new LlmException(LlmErrorKind.Authentication, e.Message, e);
                case AnthropicForbiddenException e: return new LlmException(LlmErrorKind.PermissionDenied, e.Message, e);
                case AnthropicNotFoundException e: return new LlmException(LlmErrorKind.NotFound, e.Message, e);
                case AnthropicRateLimitException e: return new LlmException(LlmErrorKind.RateLimited, e.Message, e);
                case AnthropicBadRequestException e: return new LlmException(LlmErrorKind.InvalidRequest, e.Message, e);
                case AnthropicUnprocessableEntityException e: return new LlmException(LlmErrorKind.InvalidRequest, e.Message, e);
                case Anthropic5xxException e: return new LlmException(LlmErrorKind.Server, e.Message, e);
                case AnthropicIOException e: return new LlmException(LlmErrorKind.Network, e.Message, e);
                case HttpRequestException e: return new LlmException(LlmErrorKind.Network, e.Message, e);
                case AnthropicApiException e: return new LlmException(LlmErrorKind.Unknown, e.Message, e);
                default: return null;
            }
        }
    }
}
