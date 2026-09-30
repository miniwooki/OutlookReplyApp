using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class LlmModelListerTests
    {
        private static LlmProfile Claude(string workspaceId) =>
            new LlmProfile { DisplayName = "Claude", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", WorkspaceId = workspaceId };

        private static string AnthropicPage(bool hasMore, params string[] ids) =>
            "{\"data\":[" + string.Join(",", ids.Select(id =>
                "{\"type\":\"model\",\"id\":\"" + id + "\",\"display_name\":\"" + id.ToUpperInvariant() + "\",\"created_at\":\"2026-01-01T00:00:00Z\"}"))
            + "],\"has_more\":" + (hasMore ? "true" : "false") + ",\"first_id\":\"" + ids.First() + "\",\"last_id\":\"" + ids.Last() + "\"}";

        private static string OpenAiList(params (string Id, long Created)[] models) =>
            "{\"object\":\"list\",\"data\":[" + string.Join(",", models.Select(m =>
                "{\"id\":\"" + m.Id + "\",\"object\":\"model\",\"created\":" + m.Created + ",\"owned_by\":\"test\"}")) + "]}";

        [Fact]
        public async Task Anthropic_PagesThroughList_SkipsUnsupportedModels_AndSendsHeaders()
        {
            var handler = new StubHttpHandler()
                .Respond(HttpStatusCode.OK, AnthropicPage(true, "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5-20251001"))
                .Respond(HttpStatusCode.OK, AnthropicPage(false, "claude-opus-4-8"));

            var models = await LlmModelLister.ListAsync(Claude(" wrkspc_9 "), "sk-ant-test", handler, CancellationToken.None);

            Assert.Equal(new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-opus-4-8" }, models.Select(m => m.Id));
            Assert.Equal("CLAUDE-OPUS-5-5", models[0].DisplayName);
            Assert.Equal(2, handler.Requests.Count);
            var first = handler.Requests[0];
            Assert.Equal("GET", first.Method);
            Assert.Equal("/v1/models", first.Uri.AbsolutePath);
            Assert.Contains("limit=1000", first.Uri.Query);
            Assert.Equal("sk-ant-test", first.Header("x-api-key"));
            Assert.Equal("2023-06-01", first.Header("anthropic-version"));
            Assert.Equal("wrkspc_9", first.Header("anthropic-workspace-id"));
            Assert.Contains("after_id=claude-haiku-4-5-20251001", handler.Requests[1].Uri.Query);
        }

        [Fact]
        public async Task Anthropic_NoWorkspaceId_HeaderAbsent()
        {
            var handler = new StubHttpHandler().Respond(HttpStatusCode.OK, AnthropicPage(false, "claude-opus-5"));
            var models = await LlmModelLister.ListAsync(Claude(""), "sk-ant-test", handler, CancellationToken.None);
            Assert.Equal(new[] { "claude-opus-5" }, models.Select(m => m.Id));
            Assert.Null(handler.Requests.Single().Header("anthropic-workspace-id"));
        }

        [Fact]
        public async Task Anthropic_Unauthorized_MapsToAuthentication()
        {
            var handler = new StubHttpHandler().Respond(HttpStatusCode.Unauthorized,
                "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}");
            var ex = await Assert.ThrowsAsync<LlmException>(() => LlmModelLister.ListAsync(Claude(""), "sk-bad", handler, CancellationToken.None));
            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
        }

        [Fact]
        public async Task Anthropic_KeyNotScopedToWorkspace_MapsToWorkspaceRequired()
        {
            var handler = new StubHttpHandler().Respond(HttpStatusCode.BadRequest,
                "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"This API key is not scoped to a workspace, so this request must include the anthropic-workspace-id header with the ID of the workspace to use.\"}}");
            var ex = await Assert.ThrowsAsync<LlmException>(() => LlmModelLister.ListAsync(Claude(""), "sk-ant-org", handler, CancellationToken.None));
            Assert.Equal(LlmErrorKind.WorkspaceRequired, ex.Kind);
            Assert.Contains("Workspace ID", ex.UserMessage);
        }

        [Fact]
        public async Task OpenAiCompatible_UsesBaseUrlAndBearer_FiltersNonChat_NewestFirst()
        {
            var handler = new StubHttpHandler().Respond(HttpStatusCode.OK, OpenAiList(
                ("grok-3", 1740000000), ("grok-2-image-1212", 1736000000), ("grok-4", 1752000000), ("text-embedding-3-large", 1760000000)));
            var profile = new LlmProfile { DisplayName = "xAI", Provider = LlmProviderKind.OpenAI, Model = "grok-4", BaseUrl = "https://api.x.ai/v1" };

            var models = await LlmModelLister.ListAsync(profile, "xai-test", handler, CancellationToken.None);

            Assert.Equal(new[] { "grok-4", "grok-3" }, models.Select(m => m.Id));
            var request = handler.Requests.Single();
            Assert.Equal("https://api.x.ai/v1/models", request.Uri.ToString());
            Assert.Equal("Bearer xai-test", request.Header("Authorization"));
        }

        [Fact]
        public async Task OpenAi_DefaultEndpoint_Unauthorized_MapsToAuthentication()
        {
            var handler = new StubHttpHandler().Respond(HttpStatusCode.Unauthorized,
                "{\"error\":{\"message\":\"Incorrect API key provided\",\"type\":\"invalid_request_error\"}}");
            var profile = new LlmProfile { DisplayName = "OpenAI", Provider = LlmProviderKind.OpenAI, Model = "gpt-5.1" };

            var ex = await Assert.ThrowsAsync<LlmException>(() => LlmModelLister.ListAsync(profile, "sk-bad", handler, CancellationToken.None));

            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
            Assert.Equal("https://api.openai.com/v1/models", handler.Requests.Single().Uri.ToString());
        }

        [Theory]
        [InlineData("gpt-5.1", true)]
        [InlineData("grok-4", true)]
        [InlineData("o3", true)]
        [InlineData("text-embedding-3-large", false)]
        [InlineData("tts-1-hd", false)]
        [InlineData("whisper-1", false)]
        [InlineData("dall-e-3", false)]
        [InlineData("gpt-image-1", false)]
        [InlineData("omni-moderation-latest", false)]
        [InlineData("gpt-4o-audio-preview", false)]
        [InlineData("gpt-4o-realtime-preview", false)]
        [InlineData("gpt-4o-transcribe", false)]
        [InlineData("gpt-4o-search-preview", false)]
        [InlineData("davinci-002", false)]
        [InlineData("babbage-002", false)]
        [InlineData("grok-imagine-video-1.5", false)]
        [InlineData("gpt-live-1", false)]
        [InlineData("", false)]
        public void IsChatModel(string id, bool expected)
        {
            Assert.Equal(expected, LlmModelLister.IsChatModel(id));
        }
    }
}
