using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class LlmProviderFactoryTests
    {
        [Fact]
        public void Create_ByProviderKind()
        {
            Assert.IsType<AnthropicProvider>(LlmProviderFactory.Create(
                new LlmProfile { Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5" }, "k"));
            Assert.IsType<OpenAiProvider>(LlmProviderFactory.Create(
                new LlmProfile { Provider = LlmProviderKind.OpenAI, Model = "gpt-test" }, "k"));
        }

        [Fact]
        public void Create_OpenAiWithBaseUrl_DoesNotThrow()
        {
            Assert.NotNull(LlmProviderFactory.Create(
                new LlmProfile { Provider = LlmProviderKind.OpenAI, Model = "local-model", BaseUrl = "http://localhost:8000/v1" }, "k"));
        }

        [Fact]
        public void Create_FromSecretStore_UsesStoredKey()
        {
            using (var tmp = new TempDir())
            {
                var secrets = new SecretStore(tmp.Root);
                secrets.Set("s1", "sk-ant-x");
                var provider = new LlmProviderFactory(secrets).Create(
                    new LlmProfile { DisplayName = "팀 키", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", SecretId = "s1" });
                Assert.Equal("팀 키", provider.DisplayName);
            }
        }

        [Fact]
        public void Create_MissingSecret_ThrowsAuthenticationWithProfileName()
        {
            using (var tmp = new TempDir())
            {
                var ex = Assert.Throws<LlmException>(() => new LlmProviderFactory(new SecretStore(tmp.Root)).Create(
                    new LlmProfile { DisplayName = "개인 키", Provider = LlmProviderKind.OpenAI, Model = "m", SecretId = "none" }));
                Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
                Assert.Contains("개인 키", ex.Message);
            }
        }

        [Fact]
        public void OpenAi_EmptyModel_Throws()
        {
            Assert.Throws<System.ArgumentException>(() =>
                new OpenAiProvider(new LlmProfile { Provider = LlmProviderKind.OpenAI, Model = "" }, "k"));
        }
    }
}
