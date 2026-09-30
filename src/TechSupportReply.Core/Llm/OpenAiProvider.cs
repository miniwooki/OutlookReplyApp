using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Chat;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Llm
{
    /// <summary>공식 OpenAI .NET SDK 기반 공급자. BaseUrl로 OpenAI 호환 엔드포인트도 사용할 수 있다.</summary>
    public sealed class OpenAiProvider : ILlmProvider
    {
        private readonly LlmProfile _profile;
        private readonly ChatClient _chat;

        public OpenAiProvider(LlmProfile profile, string apiKey)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(profile.Model)) throw new ArgumentException("모델명이 비어 있습니다.");
            if (string.IsNullOrWhiteSpace(apiKey)) throw new LlmException(LlmErrorKind.Authentication, "API 키가 비어 있습니다.");
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(profile.BaseUrl)) options.Endpoint = new Uri(profile.BaseUrl);
            _chat = new ChatClient(profile.Model, new ApiKeyCredential(apiKey), options);
        }

        public string DisplayName => _profile.DisplayName;

        public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var (messages, options) = Build(request);
            try
            {
                ClientResult<ChatCompletion> result = await _chat.CompleteChatAsync(messages, options, ct).ConfigureAwait(false);
                var completion = result.Value;
                if (!string.IsNullOrEmpty(completion.Refusal)) throw new LlmException(LlmErrorKind.Refusal, completion.Refusal);
                return string.Concat(completion.Content.Select(p => p.Text));
            }
            catch (Exception ex) when (Translate(ex) is LlmException mapped)
            {
                throw mapped;
            }
        }

        public async Task<string> StreamAsync(LlmRequest request, Action<string> onDelta, CancellationToken ct)
        {
            var (messages, options) = Build(request);
            var text = new StringBuilder();
            var refusal = new StringBuilder();
            try
            {
                await foreach (StreamingChatCompletionUpdate update in _chat.CompleteChatStreamingAsync(messages, options, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(update.RefusalUpdate)) refusal.Append(update.RefusalUpdate);
                    foreach (ChatMessageContentPart part in update.ContentUpdate)
                    {
                        if (string.IsNullOrEmpty(part.Text)) continue;
                        text.Append(part.Text);
                        onDelta?.Invoke(part.Text);
                    }
                }
            }
            catch (Exception ex) when (Translate(ex) is LlmException mapped)
            {
                throw mapped;
            }
            if (refusal.Length > 0 && text.Length == 0) throw new LlmException(LlmErrorKind.Refusal, refusal.ToString());
            return text.ToString();
        }

        private (List<ChatMessage> Messages, ChatCompletionOptions Options) Build(LlmRequest r)
        {
            var messages = new List<ChatMessage>();
            var system = string.Join("\n\n", new[] { r.CachedSystem, r.System }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (system.Length > 0) messages.Add(new SystemChatMessage(system));
            foreach (var m in r.Messages)
                messages.Add(m.Role == LlmRole.User ? (ChatMessage)new UserChatMessage(m.Content) : new AssistantChatMessage(m.Content));

            var options = new ChatCompletionOptions { MaxOutputTokenCount = r.MaxTokens > 0 ? r.MaxTokens : _profile.MaxTokens };
            if (!string.IsNullOrEmpty(r.JsonSchema))
                options.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(r.SchemaName, BinaryData.FromString(r.JsonSchema), jsonSchemaIsStrict: true);
            return (messages, options);
        }

        internal static LlmException Translate(Exception ex)
        {
            switch (ex)
            {
                case LlmException _:
                case OperationCanceledException _:
                    return null;
                case ClientResultException e when e.Status == 401: return new LlmException(LlmErrorKind.Authentication, e.Message, e);
                case ClientResultException e when e.Status == 403: return new LlmException(LlmErrorKind.PermissionDenied, e.Message, e);
                case ClientResultException e when e.Status == 404: return new LlmException(LlmErrorKind.NotFound, e.Message, e);
                case ClientResultException e when e.Status == 429: return new LlmException(LlmErrorKind.RateLimited, e.Message, e);
                case ClientResultException e when e.Status >= 500: return new LlmException(LlmErrorKind.Server, e.Message, e);
                case ClientResultException e when e.Status == 0: return new LlmException(LlmErrorKind.Network, e.Message, e);
                case ClientResultException e: return new LlmException(LlmErrorKind.InvalidRequest, e.Message, e);
                case HttpRequestException e: return new LlmException(LlmErrorKind.Network, e.Message, e);
                case IOException e: return new LlmException(LlmErrorKind.Network, e.Message, e);
                default: return null;
            }
        }
    }
}
