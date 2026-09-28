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

        private static KnowledgeRetriever Retriever(string root, string cache, bool withEmbedder = true, TimeSpan? syncInterval = null) =>
            new KnowledgeRetriever(new IndexCacheSync(root, cache), Catalog,
                m => withEmbedder && m?.EmbeddingModel == FakeEmbedder.Id ? new FakeEmbedder() : (IEmbedder)null, syncInterval);

        private static string[] CachedIndexFiles(string cache) =>
            Directory.GetFiles(Path.Combine(cache, "index"), "*.sqlite");

        [Fact]
        public async Task Retrieve_SameVersionDifferentHash_WhileOldIndexOpen_ServesNewContent()
        {
            using (var tmp = new TempDir())
            {
                var root = tmp.Sub("kb");
                PublishProduct(tmp, root, "ls-dyna", new Dictionary<string, string> { [@"faq\a.md"] = "접촉 첫째판" });
                using (var retriever = Retriever(root, tmp.Sub("cache"), syncInterval: TimeSpan.Zero))
                {
                    Assert.Contains("첫째판", (await retriever.RetrieveAsync("ls-dyna", "접촉", 8, 0, CancellationToken.None)).References[0].Text);

                    // 관리자가 _index를 지우고 다시 색인 → 버전은 다시 1, 해시만 다름
                    Directory.Delete(Path.Combine(root, "_index"), true);
                    PublishProduct(tmp, root, "ls-dyna", new Dictionary<string, string> { [@"faq\a.md"] = "접촉 둘째판" });

                    var r = await retriever.RetrieveAsync("ls-dyna", "접촉", 8, 0, CancellationToken.None);
                    Assert.Contains("둘째판", Assert.Single(r.References).Text);
                    Assert.DoesNotContain(r.Warnings, w => w.Contains("해시"));
                }
            }
        }

        [Fact]
        public async Task Retrieve_NewVersion_OldCacheFileDeleted()
        {
            using (var tmp = new TempDir())
            {
                var root = tmp.Sub("kb");
                var cache = tmp.Sub("cache");
                PublishProduct(tmp, root, "ls-dyna", new Dictionary<string, string> { [@"faq\a.md"] = "접촉 첫째판" });
                using (var retriever = Retriever(root, cache, syncInterval: TimeSpan.Zero))
                {
                    await retriever.RetrieveAsync("ls-dyna", "접촉", 8, 0, CancellationToken.None);
                    for (int i = 0; i < 3; i++)
                    {
                        PublishProduct(tmp, root, "ls-dyna", new Dictionary<string, string> { [@"faq\a.md"] = "접촉 개정 " + i });
                        await retriever.RetrieveAsync("ls-dyna", "접촉", 8, 0, CancellationToken.None);
                    }
                    Assert.Single(CachedIndexFiles(cache));
                }
            }
        }

        [Fact]
        public async Task Retrieve_EmbedderUnavailableThenAvailable_RetriesVector()
        {
            using (var tmp = new TempDir())
            {
                int calls = 0;
                var sync = new IndexCacheSync(Kb(tmp), tmp.Sub("cache"));
                using (var retriever = new KnowledgeRetriever(sync, Catalog, m => calls++ == 0 ? null : new FakeEmbedder(), TimeSpan.Zero))
                {
                    var first = await retriever.RetrieveAsync("ls-dyna", "접촉", 8, 0, CancellationToken.None);
                    Assert.Contains(first.Warnings, w => w.Contains("키워드 검색만"));
                    var second = await retriever.RetrieveAsync("ls-dyna", "접촉", 8, 0, CancellationToken.None);
                    Assert.DoesNotContain(second.Warnings, w => w.Contains("키워드 검색만"));
                }
            }
        }

        [Fact]
        public async Task Retrieve_WhileAnotherSyncIsSlow_UsesCacheWithoutWaiting()
        {
            using (var tmp = new TempDir())
            using (var retriever = Retriever(Kb(tmp), tmp.Sub("cache"), syncInterval: TimeSpan.Zero))
            using (var entered = new ManualResetEventSlim(false))
            using (var gate = new ManualResetEventSlim(false))
            {
                await retriever.RetrieveAsync("ls-dyna", "접촉", 8, 0, CancellationToken.None);
                retriever.SyncStarting = () =>
                {
                    entered.Set();
                    gate.Wait(TimeSpan.FromSeconds(20));
                };
                var slow = retriever.RetrieveAsync("ls-dyna", "접촉", 8, 0, CancellationToken.None);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                var fast = retriever.RetrieveAsync("ls-dyna", "접촉", 8, 0, CancellationToken.None);
                bool finished = fast.Wait(TimeSpan.FromSeconds(3));
                gate.Set();
                await slow;
                Assert.True(finished, "동기화가 진행 중이어도 캐시로 바로 검색해야 합니다.");
                Assert.NotEmpty(fast.Result.References);
            }
        }

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
