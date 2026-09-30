using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class LlmProviderFactoryTests
    {
        [Fact]
        public void ResolveEnvKey_TrimsValue_AndReturnsNullWhenMissing()
        {
            var p = new LlmProfile { ApiKeyEnvVar = " K " };
            Assert.Equal("v", LlmProviderFactory.ResolveEnvKey(p, n => n == "K" ? " v " : null));
            Assert.Null(LlmProviderFactory.ResolveEnvKey(p, _ => "  "));
            Assert.Null(LlmProviderFactory.ResolveEnvKey(p, null));
            Assert.Null(LlmProviderFactory.ResolveEnvKey(new LlmProfile { ApiKeyEnvVar = "" }, _ => "x"));
        }

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
        public void Create_MissingSecret_ThrowsNotConfiguredWithProfileName()
        {
            using (var tmp = new TempDir())
            {
                var ex = Assert.Throws<LlmException>(() => new LlmProviderFactory(new SecretStore(tmp.Root), _ => null).Create(
                    new LlmProfile { DisplayName = "개인 키", Provider = LlmProviderKind.OpenAI, Model = "m", SecretId = "none" }));
                Assert.Equal(LlmErrorKind.NotConfigured, ex.Kind);
                Assert.Contains("개인 키", ex.UserMessage);
            }
        }

        [Fact]
        public void ResolveApiKey_EnvVarWins_ThenFallsBackToSecret()
        {
            using (var tmp = new TempDir())
            {
                var secrets = new SecretStore(tmp.Root);
                secrets.Set("s1", "stored");
                var profile = new LlmProfile { Provider = LlmProviderKind.OpenAI, Model = "m", SecretId = "s1", ApiKeyEnvVar = "MY_KEY" };

                Assert.Equal("from-env", new LlmProviderFactory(secrets, n => n == "MY_KEY" ? "from-env" : null).ResolveApiKey(profile));
                Assert.Equal("stored", new LlmProviderFactory(secrets, _ => null).ResolveApiKey(profile));
            }
        }

        [Fact]
        public void Create_PassesResolvedKeyToInjectedFactory()
        {
            using (var tmp = new TempDir())
            {
                string seenKey = null;
                var fake = new FakeLlmProvider();
                var factory = new LlmProviderFactory(new SecretStore(tmp.Root), _ => "env-key", (p, k) => { seenKey = k; return fake; });
                Assert.Same(fake, factory.Create(new LlmProfile { DisplayName = "x", Provider = LlmProviderKind.OpenAI, Model = "m", ApiKeyEnvVar = "X" }));
                Assert.Equal("env-key", seenKey);
            }
        }

        [Fact]
        public void Create_EnvVarProfileWithoutValue_MentionsVariableName()
        {
            using (var tmp = new TempDir())
            {
                var ex = Assert.Throws<LlmException>(() => new LlmProviderFactory(new SecretStore(tmp.Root), _ => null).Create(
                    new LlmProfile { DisplayName = "Claude", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY" }));
                Assert.Equal(LlmErrorKind.NotConfigured, ex.Kind);
                Assert.Contains("ANTHROPIC_API_KEY", ex.UserMessage);
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
