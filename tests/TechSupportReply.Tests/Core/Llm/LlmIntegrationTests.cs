using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    [Trait("Category", "Integration")]
    public class LlmIntegrationTests
    {
        private static string Env(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        private static LlmRequest Hello()
        {
            var r = new LlmRequest { CachedSystem = "당신은 연결 테스트 도우미입니다.", MaxTokens = 2000, Effort = "low" };
            r.Messages.Add(LlmMessage.User("'연결 성공'이라고만 답하세요."));
            return r;
        }

        private static AnthropicProvider Anthropic(string key) => new AnthropicProvider(new LlmProfile
        {
            DisplayName = "it",
            Provider = LlmProviderKind.Anthropic,
            Model = Env("ANTHROPIC_TEST_MODEL") ?? "claude-opus-5",
        }, key);

        [SkippableFact]
        public async Task Anthropic_Stream_ReturnsText_AndDeltasMatch()
        {
            var key = Env("ANTHROPIC_API_KEY");
            Skip.If(key == null, "ANTHROPIC_API_KEY가 없어 건너뜀");
            var deltas = new StringBuilder();
            var text = await Anthropic(key).StreamAsync(Hello(), d => deltas.Append(d), CancellationToken.None);
            Assert.Contains("연결", text);
            Assert.Equal(text, deltas.ToString());
        }

        [SkippableFact]
        public async Task Anthropic_StructuredClassification_LsDyna()
        {
            var key = Env("ANTHROPIC_API_KEY");
            Skip.If(key == null, "ANTHROPIC_API_KEY가 없어 건너뜀");
            var mail = Mails.Create("해석 중 경고 문의", "*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE에서 초기 관통 경고가 d3hsp에 많이 나옵니다.");
            var r = await new ProductClassifier(ProductCatalog.CreateDefault()).ClassifyAsync(mail, Anthropic(key), CancellationToken.None);
            Assert.Equal(ClassificationSource.Llm, r.Source);
            Assert.Equal("ls-dyna", r.ProductId);
        }

        [SkippableFact]
        public async Task Anthropic_InvalidKey_ThrowsAuthentication()
        {
            Skip.If(Env("ANTHROPIC_API_KEY") == null, "네트워크 테스트는 ANTHROPIC_API_KEY가 있을 때만 실행");
            var ex = await Assert.ThrowsAsync<LlmException>(() => Anthropic("sk-ant-invalid").CompleteAsync(Hello(), CancellationToken.None));
            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
        }

        private static OpenAiProvider OpenAi(string key, string model) => new OpenAiProvider(new LlmProfile
        {
            DisplayName = "it-openai",
            Provider = LlmProviderKind.OpenAI,
            Model = model,
            BaseUrl = Env("OPENAI_TEST_BASE_URL") ?? "",
        }, key);

        [SkippableFact]
        public async Task OpenAi_Stream_ReturnsText_AndDeltasMatch()
        {
            var key = Env("OPENAI_API_KEY");
            var model = Env("OPENAI_TEST_MODEL");
            Skip.If(key == null || model == null, "OPENAI_API_KEY와 OPENAI_TEST_MODEL이 필요합니다");
            var deltas = new StringBuilder();
            var text = await OpenAi(key, model).StreamAsync(Hello(), d => deltas.Append(d), CancellationToken.None);
            Assert.Contains("연결", text);
            Assert.Equal(text, deltas.ToString());
        }

        [SkippableFact]
        public async Task OpenAi_StructuredClassification_Fluent()
        {
            var key = Env("OPENAI_API_KEY");
            var model = Env("OPENAI_TEST_MODEL");
            Skip.If(key == null || model == null, "OPENAI_API_KEY와 OPENAI_TEST_MODEL이 필요합니다");
            var mail = Mails.Create("계산 발산 문의", "Fluent에서 residual이 줄지 않고 계산이 발산합니다.");
            var r = await new ProductClassifier(ProductCatalog.CreateDefault()).ClassifyAsync(mail, OpenAi(key, model), CancellationToken.None);
            Assert.Equal(ClassificationSource.Llm, r.Source);
            Assert.Equal("ansys-fluent", r.ProductId);
        }
    }
}
