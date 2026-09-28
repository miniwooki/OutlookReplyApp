using TechSupportReply.Core.Llm;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class LlmExceptionTests
    {
        [Theory]
        [InlineData(LlmErrorKind.Authentication, "API 키")]
        [InlineData(LlmErrorKind.RateLimited, "한도")]
        [InlineData(LlmErrorKind.Network, "네트워크")]
        [InlineData(LlmErrorKind.Refusal, "거절")]
        [InlineData(LlmErrorKind.NotFound, "모델")]
        public void UserMessage_IsKorean(LlmErrorKind kind, string expected)
        {
            Assert.Contains(expected, new LlmException(kind, "detail").UserMessage);
        }

        [Fact]
        public void UserMessage_InvalidRequest_IncludesDetail()
        {
            Assert.Contains("bad field", new LlmException(LlmErrorKind.InvalidRequest, "bad field").UserMessage);
        }
    }
}
