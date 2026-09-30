using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class AnthropicWorkspaceTests
    {
        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;
            public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

            public CapturingHandler(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request);
                return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
            }
        }

        private static LlmRequest Hello()
        {
            var r = new LlmRequest { CachedSystem = "테스트", MaxTokens = 100 };
            r.Messages.Add(LlmMessage.User("hi"));
            return r;
        }

        private static AnthropicProvider Provider(string workspaceId, HttpMessageHandler handler) =>
            new AnthropicProvider(new LlmProfile { DisplayName = "t", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", WorkspaceId = workspaceId }, "sk-ant-test", handler);

        [Fact]
        public async Task WorkspaceId_IsSentAsHeader()
        {
            var handler = new CapturingHandler(HttpStatusCode.Unauthorized,
                "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}");
            var ex = await Assert.ThrowsAsync<LlmException>(() => Provider(" wrkspc_123 ", handler).CompleteAsync(Hello(), CancellationToken.None));
            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
            var request = handler.Requests.First();
            Assert.True(request.Headers.TryGetValues("anthropic-workspace-id", out var values));
            Assert.Equal("wrkspc_123", values.Single());
        }

        [Fact]
        public async Task NoWorkspaceId_HeaderAbsent()
        {
            var handler = new CapturingHandler(HttpStatusCode.Unauthorized,
                "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}");
            await Assert.ThrowsAsync<LlmException>(() => Provider("", handler).CompleteAsync(Hello(), CancellationToken.None));
            Assert.False(handler.Requests.First().Headers.Contains("anthropic-workspace-id"));
        }

        [Fact]
        public async Task WorkspaceRequiredError_IsTranslated()
        {
            var handler = new CapturingHandler(HttpStatusCode.BadRequest,
                "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"This API key is not scoped to a workspace, so this request must include the anthropic-workspace-id header with the ID of the workspace to use.\"}}");
            var ex = await Assert.ThrowsAsync<LlmException>(() => Provider("", handler).CompleteAsync(Hello(), CancellationToken.None));
            Assert.Equal(LlmErrorKind.WorkspaceRequired, ex.Kind);
            Assert.Contains("Workspace ID", ex.UserMessage);
        }
    }
}
