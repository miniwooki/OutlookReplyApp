using System;
using System.IO;
using System.Threading;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Loaders;
using TechSupportReply.Rag.Store;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Indexing
{
    public class IndexPublisherTests
    {
        private static IndexBuildReport BuildLocal(TempDir tmp, string productId, string text, out string workIndex)
        {
            var dir = tmp.Sub("src/" + productId);
            tmp.File($"src/{productId}/faq/a.md", text);
            workIndex = Path.Combine(tmp.Root, "work", productId + ".sqlite");
            return new IndexBuilder(DocumentLoaderRegistry.CreateDefault(), new Chunker(), new FakeEmbedder())
                .Build(productId, dir, workIndex, false, null, CancellationToken.None);
        }

        [Fact]
        public void Manifest_SaveLoad_RoundTrip()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Root, "_index", "manifest.json");
                var m = new IndexManifest { EmbeddingModel = "bge-m3-int8", Dimension = 1024 };
                m.Products.Add(new ProductIndexInfo { ProductId = "ls-dyna", File = "ls-dyna.sqlite", Version = 3, Sha256 = "abc", BuiltAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), FileCount = 2, ChunkCount = 10 });
                m.Save(path);
                var loaded = IndexManifest.Load(path);
                Assert.Equal("bge-m3-int8", loaded.EmbeddingModel);
                Assert.Equal(1024, loaded.Dimension);
                var p = loaded.Find("LS-DYNA");
                Assert.Equal(3, p.Version);
                Assert.Equal("abc", p.Sha256);
                Assert.Equal(10, p.ChunkCount);
            }
        }

        [Fact]
        public void Manifest_Load_Missing_ReturnsNull()
        {
            using (var tmp = new TempDir())
            {
                Assert.Null(IndexManifest.Load(Path.Combine(tmp.Root, "none.json")));
                var bad = tmp.File("bad.json", "{ nope");
                Assert.Throws<InvalidDataException>(() => IndexManifest.Load(bad));
            }
        }

        [Fact]
        public void Publish_First_CreatesIndexAndManifestVersion1()
        {
            using (var tmp = new TempDir())
            {
                var root = tmp.Sub("kb");
                var report = BuildLocal(tmp, "ls-dyna", "접촉 문서", out var work);
                var info = new IndexPublisher(root).Publish("ls-dyna", work, FakeEmbedder.Id, 64, report);

                Assert.Equal(1, info.Version);
                Assert.Equal(64, info.Sha256.Length);
                Assert.True(File.Exists(Path.Combine(root, "_index", "ls-dyna.sqlite")));
                Assert.False(File.Exists(Path.Combine(root, "_index", "ls-dyna.sqlite.tmp")));
                var manifest = IndexManifest.Load(KbLayout.ManifestPath(root));
                Assert.Equal(FakeEmbedder.Id, manifest.EmbeddingModel);
                Assert.Equal(1, manifest.Find("ls-dyna").FileCount);
                using (var store = SqliteIndexStore.OpenReadOnly(KbLayout.IndexFile(root, "ls-dyna")))
                    Assert.Single(store.SearchKeyword("접촉", 5));
            }
        }

        [Fact]
        public void Publish_Again_IncrementsVersionKeepsOtherProducts()
        {
            using (var tmp = new TempDir())
            {
                var root = tmp.Sub("kb");
                var publisher = new IndexPublisher(root);
                var r1 = BuildLocal(tmp, "ls-dyna", "접촉 문서", out var w1);
                publisher.Publish("ls-dyna", w1, FakeEmbedder.Id, 64, r1);
                var r2 = BuildLocal(tmp, "ansys-fluent", "수렴 문서", out var w2);
                publisher.Publish("ansys-fluent", w2, FakeEmbedder.Id, 64, r2);
                var r3 = BuildLocal(tmp, "ls-dyna", "접촉 문서 수정", out var w3);
                var info = publisher.Publish("ls-dyna", w3, FakeEmbedder.Id, 64, r3);

                Assert.Equal(2, info.Version);
                var manifest = IndexManifest.Load(KbLayout.ManifestPath(root));
                Assert.Equal(2, manifest.Products.Count);
                Assert.Equal(1, manifest.Find("ansys-fluent").Version);
            }
        }

        [Fact]
        public void PrepareWorkingCopy_CopiesExistingSharedIndex()
        {
            using (var tmp = new TempDir())
            {
                var root = tmp.Sub("kb");
                var publisher = new IndexPublisher(root);
                var workDir = tmp.Sub("work2");
                Assert.False(File.Exists(publisher.PrepareWorkingCopy("ls-dyna", workDir)));

                var report = BuildLocal(tmp, "ls-dyna", "접촉 문서", out var work);
                publisher.Publish("ls-dyna", work, FakeEmbedder.Id, 64, report);
                var copy = publisher.PrepareWorkingCopy("ls-dyna", workDir);
                Assert.True(File.Exists(copy));
                Assert.StartsWith(workDir, copy);
                using (var store = SqliteIndexStore.OpenReadOnly(copy))
                    Assert.Single(store.GetFiles());
            }
        }

        [Fact]
        public void Publish_NoChanges_KeepsVersionAndFile()
        {
            using (var tmp = new TempDir())
            {
                var root = tmp.Sub("kb");
                var publisher = new IndexPublisher(root);
                var r1 = BuildLocal(tmp, "ls-dyna", "접촉 문서", out var w1);
                var first = publisher.Publish("ls-dyna", w1, FakeEmbedder.Id, 64, r1);

                var work = publisher.PrepareWorkingCopy("ls-dyna", tmp.Sub("work3"));
                var r2 = new IndexBuilder(DocumentLoaderRegistry.CreateDefault(), new Chunker(), new FakeEmbedder())
                    .Build("ls-dyna", System.IO.Path.Combine(tmp.Root, "src", "ls-dyna"), work, false, null, CancellationToken.None);
                Assert.False(r2.HasChanges);
                var second = publisher.Publish("ls-dyna", work, FakeEmbedder.Id, 64, r2);
                Assert.Equal(first.Version, second.Version);
                Assert.Equal(first.Sha256, second.Sha256);
                Assert.Equal(first.Sha256, IndexManifest.Load(KbLayout.ManifestPath(root)).Find("ls-dyna").Sha256);
            }
        }
    }
}
