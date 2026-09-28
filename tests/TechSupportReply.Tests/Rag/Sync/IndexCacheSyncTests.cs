using System.IO;
using System.Threading;
using TechSupportReply.Core.Products;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Loaders;
using TechSupportReply.Rag.Store;
using TechSupportReply.Rag.Sync;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Sync
{
    public class IndexCacheSyncTests
    {
        /// <summary>공유 폴더에 제품 색인을 게시한다(같은 제품을 다시 부르면 새 버전).</summary>
        internal static void Publish(TempDir tmp, string root, string productId, string text)
        {
            var src = tmp.Sub("src-" + productId + "-" + System.Guid.NewGuid().ToString("N"));
            File.WriteAllText(Path.Combine(src, "a.md"), text);
            var work = Path.Combine(tmp.Root, "work", productId + ".sqlite");
            var report = new IndexBuilder(DocumentLoaderRegistry.CreateDefault(), new Chunker(), new FakeEmbedder())
                .Build(productId, src, work, true, null, CancellationToken.None);
            new IndexPublisher(root).Publish(productId, work, FakeEmbedder.Id, 64, report);
        }

        internal static string SharedRoot(TempDir tmp)
        {
            var root = tmp.Sub("kb");
            ProductCatalog.CreateDefault().Save(KbLayout.ProductsPath(root));
            tmp.File("kb/LS-DYNA/_prompt.md", "LS-DYNA 답변 지침");
            return root;
        }

        private static string SearchFirst(string indexPath, string query)
        {
            using (var store = SqliteIndexStore.OpenReadOnly(indexPath))
            {
                var hits = store.SearchKeyword(query, 1);
                return hits.Count == 0 ? null : hits[0].Text;
            }
        }

        [Fact]
        public void Sync_CopiesNewIndexAndPromptFiles()
        {
            using (var tmp = new TempDir())
            {
                var root = SharedRoot(tmp);
                Publish(tmp, root, "ls-dyna", "접촉 버전1");
                var sync = new IndexCacheSync(root, tmp.Sub("cache"));
                var result = sync.Sync(new[] { "ls-dyna", "_common" }, CancellationToken.None);

                Assert.True(result.SharedReachable);
                Assert.Equal(new[] { "ls-dyna" }, result.UpdatedProducts.ToArray());
                Assert.Empty(result.Warnings);
                Assert.Equal("접촉 버전1", SearchFirst(sync.LocalIndexPath("ls-dyna"), "접촉"));
                Assert.Null(sync.LocalIndexPath("_common"));
                Assert.Equal("LS-DYNA 답변 지침", File.ReadAllText(sync.LocalPromptPath("ls-dyna")));
                Assert.True(File.Exists(sync.LocalProductsPath));
                Assert.Equal(1, result.LocalManifest.Find("ls-dyna").Version);
            }
        }

        [Fact]
        public void Sync_SameVersion_DoesNotCopyAgain()
        {
            using (var tmp = new TempDir())
            {
                var root = SharedRoot(tmp);
                Publish(tmp, root, "ls-dyna", "접촉 버전1");
                var sync = new IndexCacheSync(root, tmp.Sub("cache"));
                sync.Sync(new[] { "ls-dyna" }, CancellationToken.None);
                Assert.Empty(sync.Sync(new[] { "ls-dyna" }, CancellationToken.None).UpdatedProducts);
            }
        }

        [Fact]
        public void Sync_NewVersion_ReplacesCachedIndex()
        {
            using (var tmp = new TempDir())
            {
                var root = SharedRoot(tmp);
                Publish(tmp, root, "ls-dyna", "접촉 버전1");
                var sync = new IndexCacheSync(root, tmp.Sub("cache"));
                sync.Sync(new[] { "ls-dyna" }, CancellationToken.None);
                var oldPath = sync.LocalIndexPath("ls-dyna");

                Publish(tmp, root, "ls-dyna", "접촉 버전2");
                var result = sync.Sync(new[] { "ls-dyna" }, CancellationToken.None);
                Assert.Equal(new[] { "ls-dyna" }, result.UpdatedProducts.ToArray());
                Assert.Equal("접촉 버전2", SearchFirst(sync.LocalIndexPath("ls-dyna"), "접촉"));
                Assert.NotEqual(oldPath, sync.LocalIndexPath("ls-dyna"));
                Assert.False(File.Exists(oldPath));
            }
        }

        [Fact]
        public void Sync_SharedUnreachable_KeepsCacheAndWarns()
        {
            using (var tmp = new TempDir())
            {
                var root = SharedRoot(tmp);
                var cache = tmp.Sub("cache");
                Publish(tmp, root, "ls-dyna", "접촉 버전1");
                new IndexCacheSync(root, cache).Sync(new[] { "ls-dyna" }, CancellationToken.None);

                var offline = new IndexCacheSync(Path.Combine(tmp.Root, "unreachable-server", "KB"), cache);
                var result = offline.Sync(new[] { "ls-dyna" }, CancellationToken.None);
                Assert.False(result.SharedReachable);
                Assert.Contains(result.Warnings, w => w.Contains("접근할 수 없") && w.Contains("캐시된 색인을 사용"));
                Assert.Equal("접촉 버전1", SearchFirst(offline.LocalIndexPath("ls-dyna"), "접촉"));
                Assert.Equal("LS-DYNA 답변 지침", File.ReadAllText(offline.LocalPromptPath("ls-dyna")));
            }
        }

        [Fact]
        public void Sync_SharedUnreachable_NoCache_WarnsNoThrow()
        {
            using (var tmp = new TempDir())
            {
                var sync = new IndexCacheSync(@"\\no-such-server-tsr\KB", tmp.Sub("cache"));
                var result = sync.Sync(new[] { "ls-dyna" }, CancellationToken.None);
                Assert.False(result.SharedReachable);
                Assert.Contains(result.Warnings, w => w.Contains("캐시된 색인이 없습니다"));
                Assert.Null(sync.LocalIndexPath("ls-dyna"));
            }
        }

        [Fact]
        public void Sync_HashMismatch_WarnsAndKeepsOld()
        {
            using (var tmp = new TempDir())
            {
                var root = SharedRoot(tmp);
                Publish(tmp, root, "ls-dyna", "접촉 버전1");
                var sync = new IndexCacheSync(root, tmp.Sub("cache"));
                sync.Sync(new[] { "ls-dyna" }, CancellationToken.None);

                Publish(tmp, root, "ls-dyna", "접촉 버전2");
                var manifest = IndexManifest.Load(KbLayout.ManifestPath(root));
                manifest.Find("ls-dyna").Sha256 = "0000";
                manifest.Save(KbLayout.ManifestPath(root));

                var result = sync.Sync(new[] { "ls-dyna" }, CancellationToken.None);
                Assert.Empty(result.UpdatedProducts);
                Assert.Contains(result.Warnings, w => w.Contains("해시"));
                Assert.Equal("접촉 버전1", SearchFirst(sync.LocalIndexPath("ls-dyna"), "접촉"));
            }
        }

        [Fact]
        public void EnsureModel_CopiesOnce()
        {
            using (var tmp = new TempDir())
            {
                var root = tmp.Sub("kb");
                tmp.File("kb/_models/fake-model/model.onnx", "onnx");
                tmp.File("kb/_models/fake-model/sentencepiece.bpe.model", "spm");
                var sync = new IndexCacheSync(root, tmp.Sub("cache"));

                var local = sync.EnsureModel("fake-model", CancellationToken.None);
                Assert.Equal("onnx", File.ReadAllText(Path.Combine(local, "model.onnx")));
                Directory.Delete(Path.Combine(root, "_models"), true);
                Assert.Equal(local, sync.EnsureModel("fake-model", CancellationToken.None));
                Assert.Null(sync.EnsureModel("missing-model", CancellationToken.None));
            }
        }
    }
}
