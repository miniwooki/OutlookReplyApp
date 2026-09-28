using System;
using Anthropic.Models.Messages;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class AnthropicProviderTests
    {
        private static LlmProfile Profile(string model) =>
            new LlmProfile { DisplayName = "t", Provider = LlmProviderKind.Anthropic, Model = model };

        [Theory]
        [InlineData("claude-haiku-4-5")]
        [InlineData("claude-sonnet-4-5")]
        [InlineData("claude-3-5-sonnet-latest")]
        [InlineData("")]
        public void Constructor_RejectsUnsupportedModels(string model)
        {
            Assert.Throws<ArgumentException>(() => new AnthropicProvider(Profile(model), "sk-ant-test"));
        }

        [Theory]
        [InlineData("claude-opus-5")]
        [InlineData("claude-sonnet-5")]
        [InlineData("claude-opus-4-8")]
        public void Constructor_AcceptsSupportedModels(string model)
        {
            Assert.Equal("t", new AnthropicProvider(Profile(model), "sk-ant-test").DisplayName);
        }

        [Fact]
        public void Constructor_EmptyKey_ThrowsAuthentication()
        {
            var ex = Assert.Throws<LlmException>(() => new AnthropicProvider(Profile("claude-opus-5"), " "));
            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
        }

        [Fact]
        public void BuildParams_RequiresCachedSystem()
        {
            var provider = new AnthropicProvider(Profile("claude-opus-5"), "sk-ant-test");
            var request = new LlmRequest();
            request.Messages.Add(LlmMessage.User("hi"));
            Assert.Throws<ArgumentException>(() => provider.BuildParams(request));
        }

        [Fact]
        public void BuildParams_WithSchema_Builds()
        {
            var provider = new AnthropicProvider(Profile("claude-opus-5"), "sk-ant-test");
            var request = new LlmRequest { CachedSystem = "sys", System = "vol", JsonSchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}" };
            request.Messages.Add(LlmMessage.User("hi"));
            Assert.NotNull(provider.BuildParams(request));
        }

        [Theory]
        [InlineData("low")]
        [InlineData("HIGH")]
        [InlineData("max")]
        [InlineData(null)]
        [InlineData("weird")]
        public void ToEffort_MapsValues(string value)
        {
            var expected = value == null || value == "weird" ? Effort.Medium
                : value.ToLowerInvariant() == "low" ? Effort.Low
                : value.ToLowerInvariant() == "high" ? Effort.High
                : Effort.Max;
            Assert.Equal(expected, AnthropicProvider.ToEffort(value));
        }
    }
}
