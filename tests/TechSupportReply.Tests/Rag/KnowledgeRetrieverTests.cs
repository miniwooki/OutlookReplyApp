using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Products;
using TechSupportReply.Rag;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Loaders;
using TechSupportReply.Rag.Sync;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag
{
    public class KnowledgeRetrieverTests
    {
        private static readonly ProductCatalog Catalog = ProductCatalog.CreateDefault();

        private static void PublishProduct(TempDir tmp, string root, string productId, Dictionary<string, string> files)
        {
            var src = tmp.Sub("src-" + productId);
            foreach (var f in files)
            {
                var path = Path.Combine(src, f.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, f.Value);
            }
            var work = Path.Combine(tmp.Root, "work", productId + ".sqlite");
            var report = new IndexBuilder(DocumentLoaderRegistry.CreateDefault(), new Chunker(), new FakeEmbedder())
                .Build(productId, src, work, true, null, CancellationToken.None);
            new IndexPublisher(root).Publish(productId, work, FakeEmbedder.Id, 64, report);
        }

        private static string Kb(TempDir tmp)
        {
            var root = tmp.Sub("kb");
            PublishProduct(tmp, root, "ls-dyna", new Dictionary<string, string>
            {
                [@"faq\contact.md"] = "# 접촉 관통\n초기 관통 경고는 IGNORE=1 또는 SOFT=2로 해결합니다.",
                [@"faq\mass.md"] = "# 질량 스케일링\nDT2MS로 설정합니다.",
                [@"replies\old.txt"] = "질문:\n접촉 관통 경고\n\n답변:\n안녕하세요. SOFT=2를 권장드립니다.",
            });
            PublishProduct(tmp, root, "ansys-fluent", new Dictionary<string, string>
            {
                [@"faq\conv.md"] = "# 접촉 열저항과 수렴\n접촉 열저항이 크면 수렴이 느립니다.",
            });
            PublishProduct(tmp, root, "_common", new Dictionary<string, string>
            {
                [@"faq\license.md"] = "# 라이선스\nansyslmd 서버 접촉 불가 시 방화벽을 확인하세요.",
            });
            return root;
        }

        private static KnowledgeRetriever Retriever(string root, string cache, bool withEmbedder = true) =>
            new KnowledgeRetriever(new IndexCacheSync(root, cache), Catalog,
                m => withEmbedder && m?.EmbeddingModel == FakeEmbedder.Id ? new FakeEmbedder() : (IEmbedder)null);

        [Fact]
        public async Task Retrieve_ReturnsReferencesFromProductAndCommon_AndStyleExamples()
        {
            using (var tmp = new TempDir())
            using (var retriever = Retriever(Kb(tmp), tmp.Sub("cache")))
            {
                var r = await retriever.RetrieveAsync("ls-dyna", "접촉 관통 경고 라이선스 서버", 8, 3, CancellationToken.None);
                Assert.Contains(r.References, c => c.ProductId == "ls-dyna" && c.SourceFile == @"faq\contact.md");
                Assert.Contains(r.References, c => c.ProductId == "_common" && c.Citation == "license.md");
                Assert.All(r.References, c => Assert.False(c.IsReplyExample));
                var style = Assert.Single(r.StyleExamples);
                Assert.True(style.IsReplyExample);
                Assert.Contains("SOFT=2를 권장", style.Text);
                Assert.Empty(r.Warnings);
            }
        }

        [Fact]
        public async Task Retrieve_IrrelevantCommonChunk_RanksBelowRelevantProductChunks()
        {
            using (var tmp = new TempDir())
            {
                var root = tmp.Sub("kb");
                PublishProduct(tmp, root, "ls-dyna", new Dictionary<string, string>
                {
                    [@"faq\a.md"] = "초기 관통 경고는 IGNORE=1로 무시합니다.",
                    [@"faq\b.md"] = "관통 경고가 계속되면 SOFT=2를 씁니다.",
                    [@"faq\c.md"] = "질량 스케일링은 DT2MS로 설정합니다.",
                });
                PublishProduct(tmp, root, "_common", new Dictionary<string, string>
                {
                    [@"faq\license.md"] = "라이선스 서버 방화벽 포트 확인",
                });
                using (var retriever = Retriever(root, tmp.Sub("cache")))
                {
                    var refs = (await retriever.RetrieveAsync("ls-dyna", "관통 경고", 8, 0, CancellationToken.None)).References;
                    Assert.Equal(new[] { @"faq\a.md", @"faq\b.md" }, refs.Take(2).Select(c => c.SourceFile).ToArray());
                    Assert.True(refs.FindIndex(c => c.ProductId == "_common") > 1);
                }
            }
        }

        [Fact]
        public async Task Retrieve_ProductChanged_ReturnsDifferentEvidence()
        {
            using (var tmp = new TempDir())
            using (var retriever = Retriever(Kb(tmp), tmp.Sub("cache")))
            {
                var dyna = await retriever.RetrieveAsync("ls-dyna", "접촉", 8, 3, CancellationToken.None);
                var fluent = await retriever.RetrieveAsync("ansys-fluent", "접촉", 8, 3, CancellationToken.None);
                Assert.Contains(dyna.References, c => c.ProductId == "ls-dyna");
                Assert.DoesNotContain(dyna.References, c => c.ProductId == "ansys-fluent");
                Assert.Contains(fluent.References, c => c.ProductId == "ansys-fluent");
                Assert.DoesNotContain(fluent.References, c => c.ProductId == "ls-dyna");
                Assert.Empty(fluent.StyleExamples);
            }
        }

        [Fact]
        public async Task Retrieve_SharedUnreachableAndNoCache_ReturnsEmptyWithWarning()
        {
            using (var tmp = new TempDir())
            using (var retriever = Retriever(Path.Combine(tmp.Root, "offline"), tmp.Sub("cache")))
            {
                var r = await retriever.RetrieveAsync("ls-dyna", "접촉", 8, 3, CancellationToken.None);
                Assert.Empty(r.References);
                Assert.Empty(r.StyleExamples);
                Assert.Contains(r.Warnings, w => w.Contains("접근할 수 없"));
                Assert.Contains(r.Warnings, w => w.Contains("RAG 없이"));
            }
        }

        [Fact]
        public async Task Retrieve_SharedUnreachable_UsesCachedIndex()
        {
            using (var tmp = new TempDir())
            {
                var root = Kb(tmp);
                var cache = tmp.Sub("cache");
                using (var online = Retriever(root, cache))
                    await online.RetrieveAsync("ls-dyna", "접촉", 8, 3, CancellationToken.None);

                using (var offline = Retriever(Path.Combine(tmp.Root, "offline"), cache))
                {
                    var r = await offline.RetrieveAsync("ls-dyna", "접촉", 8, 3, CancellationToken.None);
                    Assert.Contains(r.References, c => c.ProductId == "ls-dyna");
                    Assert.Contains(r.Warnings, w => w.Contains("캐시된 색인을 사용"));
                }
            }
        }

        [Fact]
        public async Task Retrieve_NoEmbedder_KeywordOnlyWithWarning()
        {
            using (var tmp = new TempDir())
            using (var retriever = Retriever(Kb(tmp), tmp.Sub("cache"), withEmbedder: false))
            {
                var r = await retriever.RetrieveAsync("ls-dyna", "접촉 관통", 8, 3, CancellationToken.None);
                Assert.NotEmpty(r.References);
                Assert.Single(r.Warnings, w => w.Contains("키워드 검색만"));
            }
        }
    }
}
