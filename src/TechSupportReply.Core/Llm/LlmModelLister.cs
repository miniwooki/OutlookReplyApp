using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Models;
using OpenAI;
using OpenAI.Models;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Llm
{
    /// <summary>공급자의 모델 목록 API가 돌려준 모델 하나.</summary>
    public sealed class ModelListing
    {
        public ModelListing(string id, string displayName, DateTimeOffset? createdAt)
        {
            Id = id ?? "";
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName;
            CreatedAt = createdAt;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public DateTimeOffset? CreatedAt { get; }
    }

    /// <summary>
    /// 설정의 [모델 목록 불러오기]. 프로필의 API 키로 쓸 수 있는 모델을 공급자 API에서 받아온다.
    /// Anthropic은 GET /v1/models(페이지 단위, 최신순), OpenAI 호환(OpenAI·xAI)은 GET {BaseUrl}/models를 쓴다.
    /// 네트워크 호출이므로 UI 스레드에서 기다리지 않는다. 실패하면 LlmException을 던진다.
    /// </summary>
    public static class LlmModelLister
    {
        internal const int AnthropicPageLimit = 1000;
        internal const int MaxPages = 10;

        /// <summary>대화형 텍스트 생성에 쓰지 않는 OpenAI 호환 모델 ID에 들어가는 단어.</summary>
        internal static readonly string[] NonChatMarkers =
        {
            "embedding", "tts", "whisper", "dall-e", "image", "moderation", "audio", "realtime", "transcribe", "search", "davinci", "babbage",
            "video", "imagine", "live",
        };

        public static Task<IReadOnlyList<ModelListing>> ListAsync(LlmProfile profile, string apiKey, CancellationToken ct) =>
            ListAsync(profile, apiKey, null, ct);

        /// <summary>handler는 테스트용이다. null이면 SDK 기본 전송을 쓴다.</summary>
        internal static Task<IReadOnlyList<ModelListing>> ListAsync(LlmProfile profile, string apiKey, HttpMessageHandler handler, CancellationToken ct)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(apiKey)) throw new LlmException(LlmErrorKind.Authentication, "API 키가 비어 있습니다.");
            switch (profile.Provider)
            {
                case LlmProviderKind.Anthropic: return ListAnthropicAsync(profile, apiKey.Trim(), handler, ct);
                case LlmProviderKind.OpenAI: return ListOpenAiAsync(profile, apiKey.Trim(), handler, ct);
                default: throw new NotSupportedException($"지원하지 않는 공급자: {profile.Provider}");
            }
        }

        internal static bool IsChatModel(string id) =>
            !string.IsNullOrWhiteSpace(id) && !NonChatMarkers.Any(m => id.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);

        private static async Task<IReadOnlyList<ModelListing>> ListAnthropicAsync(LlmProfile profile, string apiKey, HttpMessageHandler handler, CancellationToken ct)
        {
            var result = new List<ModelListing>();
            using (var http = AnthropicProvider.CreateHttpClient((profile.WorkspaceId ?? "").Trim(), handler))
            {
                var client = new AnthropicClient { ApiKey = apiKey, HttpClient = http };
                try
                {
                    ModelListPage page = await client.Models.List(new ModelListParams { Limit = AnthropicPageLimit }, ct).ConfigureAwait(false);
                    for (int pages = 1; ; pages++)
                    {
                        foreach (ModelInfo m in page.Items)
                            if (AnthropicProvider.IsSupportedModel(m.ID)) result.Add(new ModelListing(m.ID, m.DisplayName, m.CreatedAt));
                        if (!page.HasNext() || pages >= MaxPages) break;
                        page = await page.Next(ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (AnthropicProvider.Translate(ex) is LlmException mapped)
                {
                    throw mapped;
                }
            }
            return Distinct(result);
        }

        private static async Task<IReadOnlyList<ModelListing>> ListOpenAiAsync(LlmProfile profile, string apiKey, HttpMessageHandler handler, CancellationToken ct)
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(profile.BaseUrl)) options.Endpoint = new Uri(profile.BaseUrl.Trim());
            var http = handler == null ? null : new HttpClient(handler, disposeHandler: false);
            try
            {
                if (http != null) options.Transport = new HttpClientPipelineTransport(http);
                var client = new OpenAIModelClient(new ApiKeyCredential(apiKey), options);
                ClientResult<OpenAIModelCollection> response = await client.GetModelsAsync(ct).ConfigureAwait(false);
                return Distinct(response.Value
                    .Where(m => IsChatModel(m.Id))
                    .OrderByDescending(m => m.CreatedAt)
                    .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new ModelListing(m.Id, m.Id, m.CreatedAt)));
            }
            catch (Exception ex) when (OpenAiProvider.Translate(ex) is LlmException mapped)
            {
                throw mapped;
            }
            finally
            {
                http?.Dispose();
            }
        }

        private static IReadOnlyList<ModelListing> Distinct(IEnumerable<ModelListing> models)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return models.Where(m => m.Id.Length > 0 && seen.Add(m.Id)).ToList();
        }
    }
}
