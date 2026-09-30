using System;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class LlmConnectionTesterTests
    {
        [Fact]
        public async Task Success_ReportsResponse()
        {
            var llm = new FakeLlmProvider().Enqueue("OK");
            var r = await LlmConnectionTester.TestAsync(llm, CancellationToken.None);
            Assert.True(r.Success);
            Assert.Contains("연결 성공", r.Message);
            Assert.Contains("OK", r.Message);
            Assert.Single(llm.Requests);
            Assert.Equal("low", llm.Requests[0].Effort);
        }

        [Fact]
        public async Task LlmException_UsesUserMessage()
        {
            var llm = new FakeLlmProvider { ThrowOnCall = new LlmException(LlmErrorKind.Authentication, "401") };
            var r = await LlmConnectionTester.TestAsync(llm, CancellationToken.None);
            Assert.False(r.Success);
            Assert.Equal(new LlmException(LlmErrorKind.Authentication, "401").UserMessage, r.Message);
        }

        [Fact]
        public async Task OtherException_ReportsMessage()
        {
            var llm = new FakeLlmProvider { ThrowOnCall = new InvalidOperationException("boom") };
            var r = await LlmConnectionTester.TestAsync(llm, CancellationToken.None);
            Assert.False(r.Success);
            Assert.Contains("boom", r.Message);
        }

        [Fact]
        public async Task Timeout_ReportsTimeout()
        {
            var llm = new FakeLlmProvider { Delay = TimeSpan.FromSeconds(10) };
            var r = await LlmConnectionTester.TestAsync(llm, CancellationToken.None, TimeSpan.FromMilliseconds(100));
            Assert.False(r.Success);
            Assert.Contains("시간", r.Message);
        }
    }
}
