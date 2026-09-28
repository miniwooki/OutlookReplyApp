using System;
using System.IO;
using System.Linq;
using TechSupportReply.Rag.Search;
using TechSupportReply.Rag.Store;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Search
{
    public class HybridRetrieverTests
    {
        private static readonly DateTime Mtime = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        private static SqliteIndexStore CreateStore(TempDir tmp, FakeEmbedder embedder, string model = FakeEmbedder.Id)
        {
            var store = SqliteIndexStore.Create(Path.Combine(tmp.Root, "p.sqlite"));
            store.SetMeta("embedding_model", model);
            store.SetMeta("dimension", embedder.Dimension.ToString());
            NewChunk C(string text, DocType type = DocType.Reference) => new NewChunk { DocType = type, Title = "t", Text = text, Vector = embedder.Embed(text) };
            store.UpsertFile("manual.pdf", 1, Mtime, "h", new[]
            {
                C("initial penetration warning in contact definition"),
                C("license server installation guide"),
                C("접촉 두께 설정 방법"),
            });
            store.UpsertFile(@"replies\a.eml", 1, Mtime, "h", new[] { C("접촉 문제 답변 예시", DocType.Reply) });
            return store;
        }

        [Fact]
        public void Rrf_CombinesRankings()
        {
            var fused = Rrf.Fuse(new[] { new long[] { 1, 2, 3 }, new long[] { 3, 1 } });
            Assert.Equal(new long[] { 1, 3, 2 }, fused.Select(f => f.Key).ToArray());
            Assert.Equal(1.0 / 61 + 1.0 / 62, fused[0].Value, 10);
        }

        [Fact]
        public void Rrf_ItemInBothListsWins()
        {
            var fused = Rrf.Fuse(new[] { new long[] { 1, 2 }, new long[] { 3, 2 } });
            Assert.Equal(2, fused[0].Key);
        }

        [Fact]
        public void Search_KeywordOnly_WhenNoEmbedder()
        {
            using (var tmp = new TempDir())
            using (var store = CreateStore(tmp, new FakeEmbedder()))
            {
                var retriever = new HybridRetriever(store, null);
                Assert.False(retriever.VectorEnabled);
                Assert.Contains("임베딩 모델이 없어", retriever.DisabledReason);
                var hits = retriever.Search("접촉", 5);
                Assert.Equal(2, hits.Count);
                Assert.All(hits, h => Assert.Contains("접촉", h.Text));
            }
        }

        [Fact]
        public void Search_VectorFindsSemanticMatchWithoutKeywordOverlap()
        {
            using (var tmp = new TempDir())
            {
                var embedder = new FakeEmbedder();
                using (var store = CreateStore(tmp, embedder))
                {
                    Assert.Empty(store.SearchKeyword("초기 관통 경고", 5));
                    var retriever = new HybridRetriever(store, embedder);
                    Assert.True(retriever.VectorEnabled);
                    var top = retriever.Search("초기 관통 경고", 1, DocType.Reference).Single();
                    Assert.Equal("initial penetration warning in contact definition", top.Text);
                    Assert.True(top.Score > 0);
                }
            }
        }

        [Fact]
        public void Search_ModelMismatch_DisablesVectorWithReason()
        {
            using (var tmp = new TempDir())
            {
                var embedder = new FakeEmbedder();
                using (var store = CreateStore(tmp, embedder, "bge-m3-int8"))
                {
                    var retriever = new HybridRetriever(store, embedder);
                    Assert.False(retriever.VectorEnabled);
                    Assert.Contains("bge-m3-int8", retriever.DisabledReason);
                    Assert.NotEmpty(retriever.Search("접촉", 5));
                }
            }
        }

        [Fact]
        public void Search_TypeFilter()
        {
            using (var tmp = new TempDir())
            {
                var embedder = new FakeEmbedder();
                using (var store = CreateStore(tmp, embedder))
                {
                    var hits = new HybridRetriever(store, embedder).Search("접촉 문제", 5, DocType.Reply);
                    Assert.All(hits, h => Assert.Equal(DocType.Reply, h.DocType));
                    Assert.Equal("접촉 문제 답변 예시", hits.First().Text);
                }
            }
        }
    }
}
