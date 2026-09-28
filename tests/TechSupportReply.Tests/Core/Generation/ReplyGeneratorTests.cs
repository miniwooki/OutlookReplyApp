using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Generation;
using TechSupportReply.Core.Knowledge;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Generation
{
    public class ReplyGeneratorTests
    {
        private static readonly ProductCatalog Catalog = ProductCatalog.CreateDefault();

        private static ReplyGenerator Generator(IKnowledgeRetriever retriever) =>
            new ReplyGenerator(Catalog, retriever, p => p.Id + " 지침", new AppSettings());

        private static ReplyRequest Request(string productId = "ls-dyna") => new ReplyRequest
        {
            Mail = Mails.Create("접촉 관통 문의", "안녕하세요.\n초기 관통 경고가 납니다.\n\n-----Original Message-----\n이전 스레드"),
            ProductId = productId,
        };

        [Fact]
        public async Task Generate_StreamsAndReturnsText()
        {
            var llm = new FakeLlmProvider().Enqueue("안녕하세요. SOFT=2를 사용해 보세요.");
            var deltas = new StringBuilder();
            var result = await Generator(new FakeRetriever()).GenerateAsync(Request(), llm, d => deltas.Append(d), CancellationToken.None);
            Assert.Equal("안녕하세요. SOFT=2를 사용해 보세요.", result.Text);
            Assert.Equal(result.Text, deltas.ToString());
            Assert.Equal("ls-dyna", result.ProductId);
        }

        [Fact]
        public async Task Generate_UsesSelectedProductForRetrievalAndGuide()
        {
            var retriever = new FakeRetriever();
            retriever.Result.References.Add(new KnowledgeChunk { ProductId = "ansys-fluent", SourceFile = "conv.md", Text = "완화 계수" });
            var llm = new FakeLlmProvider().Enqueue("답변");
            var result = await Generator(retriever).GenerateAsync(Request("ansys-fluent"), llm, null, CancellationToken.None);

            var call = Assert.Single(retriever.Calls);
            Assert.Equal("ansys-fluent", call.ProductId);
            Assert.StartsWith("접촉 관통 문의", call.Query);
            Assert.Contains("초기 관통 경고", call.Query);
            Assert.DoesNotContain("이전 스레드", call.Query);
            Assert.Contains("ansys-fluent 지침", llm.Requests[0].CachedSystem);
            Assert.Contains("완화 계수", llm.Requests[0].Messages[0].Content);
            Assert.Single(result.References);
        }

        [Fact]
        public async Task Generate_RetrieverThrows_WarnsAndStillGenerates()
        {
            var retriever = new FakeRetriever { Throw = new IOException("네트워크 경로를 찾을 수 없습니다") };
            var llm = new FakeLlmProvider().Enqueue("RAG 없는 답변");
            var result = await Generator(retriever).GenerateAsync(Request(), llm, null, CancellationToken.None);
            Assert.Equal("RAG 없는 답변", result.Text);
            Assert.Contains(result.Warnings, w => w.Contains("지식 검색") && w.Contains("RAG 없이"));
            Assert.Contains("참고 자료 없음", llm.Requests[0].Messages[0].Content);
        }

        [Fact]
        public async Task Generate_UseRagFalse_SkipsRetrieval()
        {
            var retriever = new FakeRetriever();
            var request = Request();
            request.UseRag = false;
            await Generator(retriever).GenerateAsync(request, new FakeLlmProvider().Enqueue("x"), null, CancellationToken.None);
            Assert.Empty(retriever.Calls);
        }

        [Fact]
        public async Task Generate_UnknownProduct_FallsBackToCommonWithWarning()
        {
            var retriever = new FakeRetriever();
            var result = await Generator(retriever).GenerateAsync(Request("no-such"), new FakeLlmProvider().Enqueue("x"), null, CancellationToken.None);
            Assert.Equal(ProductCatalog.CommonId, result.ProductId);
            Assert.Equal(ProductCatalog.CommonId, retriever.Calls[0].ProductId);
            Assert.Contains(result.Warnings, w => w.Contains("no-such"));
        }

        [Fact]
        public async Task Generate_LlmError_Propagates()
        {
            var llm = new FakeLlmProvider { ThrowOnCall = new LlmException(LlmErrorKind.Authentication, "401") };
            var ex = await Assert.ThrowsAsync<LlmException>(() => Generator(new FakeRetriever()).GenerateAsync(Request(), llm, null, CancellationToken.None));
            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
        }

        [Fact]
        public async Task Generate_Cancellation_Propagates()
        {
            var llm = new FakeLlmProvider { Delay = TimeSpan.FromSeconds(5) }.Enqueue("x");
            using (var cts = new CancellationTokenSource(50))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    Generator(new FakeRetriever()).GenerateAsync(Request(), llm, null, cts.Token));
        }
    }
}
