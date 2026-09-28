using System;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Products
{
    public class ProductClassifierTests
    {
        private static readonly ProductCatalog Catalog = ProductCatalog.CreateDefault();
        private static readonly global::TechSupportReply.Core.Models.MailSnapshot DynaMail =
            Mails.Create("LS-DYNA 접촉 문의", "*CONTACT 카드 사용 중 d3hsp 경고");

        [Fact]
        public async Task NoLlm_ReturnsKeywordResult()
        {
            var r = await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, null, CancellationToken.None);
            Assert.Equal("ls-dyna", r.ProductId);
            Assert.Equal(ClassificationSource.Keyword, r.Source);
        }

        [Fact]
        public async Task ValidLlmJson_ReturnsLlmResult()
        {
            var llm = new FakeLlmProvider().Enqueue("{\"productId\":\"ansys-fluent\",\"confidence\":0.9,\"reason\":\"Fluent 수렴 문의\"}");
            var r = await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, CancellationToken.None);
            Assert.Equal("ansys-fluent", r.ProductId);
            Assert.Equal(ClassificationSource.Llm, r.Source);
            Assert.Equal(0.9, r.Confidence);
            Assert.Equal("Fluent 수렴 문의", r.Reason);
        }

        [Fact]
        public async Task PercentConfidence_IsNormalized()
        {
            var llm = new FakeLlmProvider().Enqueue("{\"productId\":\"ls-dyna\",\"confidence\":85,\"reason\":\"x\"}");
            Assert.Equal(0.85, (await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, CancellationToken.None)).Confidence);
        }

        [Theory]
        [InlineData("{\"productId\":\"unknown-product\",\"confidence\":0.9,\"reason\":\"x\"}")]
        [InlineData("not json at all")]
        [InlineData("")]
        public async Task UnusableLlmOutput_FallsBackToKeywords(string output)
        {
            var llm = new FakeLlmProvider().Enqueue(output);
            var r = await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, CancellationToken.None);
            Assert.Equal("ls-dyna", r.ProductId);
            Assert.Equal(ClassificationSource.Keyword, r.Source);
            Assert.Contains("해석할 수 없어", r.Reason);
        }

        [Fact]
        public async Task LlmError_FallsBackWithUserMessage()
        {
            var llm = new FakeLlmProvider { ThrowOnCall = new LlmException(LlmErrorKind.RateLimited, "429") };
            var r = await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, CancellationToken.None);
            Assert.Equal(ClassificationSource.Keyword, r.Source);
            Assert.Contains("한도", r.Reason);
        }

        [Fact]
        public async Task LlmTimeout_FallsBack()
        {
            var llm = new FakeLlmProvider { Delay = TimeSpan.FromSeconds(5) }.Enqueue("{}");
            var r = await new ProductClassifier(Catalog, TimeSpan.FromMilliseconds(100)).ClassifyAsync(DynaMail, llm, CancellationToken.None);
            Assert.Equal(ClassificationSource.Keyword, r.Source);
            Assert.Contains("시간 초과", r.Reason);
        }

        [Fact]
        public async Task UserCancellation_Propagates()
        {
            var llm = new FakeLlmProvider { Delay = TimeSpan.FromSeconds(5) };
            using (var cts = new CancellationTokenSource(50))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, cts.Token));
            }
        }

        [Fact]
        public async Task Request_HasSchemaWithAllProductIds_AndKeywordHints()
        {
            var llm = new FakeLlmProvider().Enqueue("{\"productId\":\"ls-dyna\",\"confidence\":1,\"reason\":\"x\"}");
            await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, CancellationToken.None);
            var req = llm.Requests[0];
            foreach (var p in Catalog.Products) Assert.Contains("\"" + p.Id + "\"", req.JsonSchema);
            Assert.Contains("ls-dyna(", req.Messages[0].Content);
            Assert.Equal("low", req.Effort);
            Assert.False(string.IsNullOrWhiteSpace(req.CachedSystem));
        }
    }
}
