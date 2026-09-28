using System;
using System.IO;
using System.Linq;
using TechSupportReply.Rag.Store;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Store
{
    public class SqliteIndexStoreTests
    {
        private static readonly DateTime Mtime = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        private static NewChunk Chunk(string text, DocType type = DocType.Reference, float[] vector = null, string title = "t", int? page = null) =>
            new NewChunk { DocType = type, Title = title, Page = page, Text = text, Vector = vector ?? new float[] { 1, 0, 0 } };

        [Fact]
        public void Create_ThenReopenReadOnly_KeepsMeta()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Root, "a.sqlite");
                using (var store = SqliteIndexStore.Create(path)) store.SetMeta("embedding_model", "bge-m3-int8");
                using (var store = SqliteIndexStore.OpenReadOnly(path))
                {
                    Assert.Equal("bge-m3-int8", store.GetMeta("embedding_model"));
                    Assert.Null(store.GetMeta("missing"));
                }
            }
        }

        [Fact]
        public void UpsertFile_ReplacesChunks()
        {
            using (var tmp = new TempDir())
            using (var store = SqliteIndexStore.Create(Path.Combine(tmp.Root, "a.sqlite")))
            {
                store.UpsertFile(@"faq\a.md", 10, Mtime, "h1", new[] { Chunk("첫번째 내용"), Chunk("두번째 내용") });
                store.UpsertFile(@"faq\a.md", 12, Mtime.AddDays(1), "h2", new[] { Chunk("교체된 내용") });
                Assert.Equal(1, store.ChunkCount);
                var file = Assert.Single(store.GetFiles());
                Assert.Equal(@"faq\a.md", file.RelativePath);
                Assert.Equal(12, file.Size);
                Assert.Equal("h2", file.Hash);
                Assert.Equal(Mtime.AddDays(1), file.MtimeUtc);
                Assert.Empty(store.SearchKeyword("첫번째", 10));
                Assert.Single(store.SearchKeyword("교체된", 10));
            }
        }

        [Fact]
        public void RemoveFile_DeletesChunksVectorsAndFts()
        {
            using (var tmp = new TempDir())
            using (var store = SqliteIndexStore.Create(Path.Combine(tmp.Root, "a.sqlite")))
            {
                store.UpsertFile("a.md", 1, Mtime, "h", new[] { Chunk("삭제될 문서") });
                store.UpsertFile("b.md", 1, Mtime, "h", new[] { Chunk("남는 문서") });
                store.RemoveFile("a.md");
                Assert.Equal(1, store.ChunkCount);
                Assert.Single(store.LoadVectors());
                Assert.Empty(store.SearchKeyword("삭제될", 10));
                Assert.Equal(new[] { "b.md" }, store.GetFiles().Select(f => f.RelativePath).ToArray());
            }
        }

        [Fact]
        public void SearchKeyword_FindsKeywordCard()
        {
            using (var tmp = new TempDir())
            using (var store = SqliteIndexStore.Create(Path.Combine(tmp.Root, "a.sqlite")))
            {
                store.UpsertFile("m.pdf", 1, Mtime, "h", new[]
                {
                    Chunk("*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE 카드 설명", page: 3),
                    Chunk("*MAT_ELASTIC 재료 설명"),
                });
                var hit = Assert.Single(store.SearchKeyword("contact_automatic_surface_to_surface", 10));
                Assert.Equal(3, hit.Page);
                Assert.Equal("m.pdf", hit.RelativePath);
                Assert.True(hit.Score > 0);
                Assert.Single(store.SearchKeyword("CONTACT", 10));
            }
        }

        [Fact]
        public void SearchKeyword_FindsKoreanTwoSyllableTerm()
        {
            using (var tmp = new TempDir())
            using (var store = SqliteIndexStore.Create(Path.Combine(tmp.Root, "a.sqlite")))
            {
                store.UpsertFile("a.md", 1, Mtime, "h", new[] { Chunk("접촉 정의에서 초기 관통이 발생합니다."), Chunk("계산이 수렴하지 않습니다."), Chunk("라이선스 설치") });
                Assert.Contains("접촉", Assert.Single(store.SearchKeyword("접촉", 10)).Text);
                Assert.Contains("수렴", Assert.Single(store.SearchKeyword("수렴", 10)).Text);
            }
        }

        [Fact]
        public void SearchKeyword_FilterByDocType()
        {
            using (var tmp = new TempDir())
            using (var store = SqliteIndexStore.Create(Path.Combine(tmp.Root, "a.sqlite")))
            {
                store.UpsertFile("a.md", 1, Mtime, "h", new[] { Chunk("접촉 참고 자료") });
                store.UpsertFile(@"replies\r.eml", 1, Mtime, "h", new[] { Chunk("접촉 답변 예시", DocType.Reply) });
                Assert.Equal(DocType.Reply, Assert.Single(store.SearchKeyword("접촉", 10, DocType.Reply)).DocType);
                Assert.Equal(DocType.Reference, Assert.Single(store.SearchKeyword("접촉", 10, DocType.Reference)).DocType);
                Assert.Equal(2, store.SearchKeyword("접촉", 10).Count);
            }
        }

        [Fact]
        public void SearchKeyword_StopWordsOnly_ReturnsEmpty()
        {
            using (var tmp = new TempDir())
            using (var store = SqliteIndexStore.Create(Path.Combine(tmp.Root, "a.sqlite")))
            {
                store.UpsertFile("a.md", 1, Mtime, "h", new[] { Chunk("안녕하세요 감사합니다") });
                Assert.Empty(store.SearchKeyword("안녕하세요", 10));
                Assert.Empty(store.SearchKeyword("", 10));
            }
        }

        [Fact]
        public void LoadVectors_RoundTrips()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Root, "a.sqlite");
                using (var store = SqliteIndexStore.Create(path))
                    store.UpsertFile("a.md", 1, Mtime, "h", new[] { Chunk("x", vector: new[] { 0.5f, -0.25f }), Chunk("y", DocType.Reply, new[] { 1f, 2f }) });
                using (var store = SqliteIndexStore.OpenReadOnly(path))
                {
                    var all = store.LoadVectors();
                    Assert.Equal(2, all.Count);
                    var reply = Assert.Single(store.LoadVectors(DocType.Reply));
                    Assert.Equal(new[] { 1f, 2f }, reply.Value);
                    var chunks = store.GetChunks(new[] { reply.Key, all[0].Key });
                    Assert.Equal(new[] { "y", "x" }, chunks.Select(c => c.Text).ToArray());
                }
            }
        }

        [Fact]
        public void OpenReadOnly_WriteThrows()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Root, "a.sqlite");
                SqliteIndexStore.Create(path).Dispose();
                using (var store = SqliteIndexStore.OpenReadOnly(path))
                    Assert.ThrowsAny<Exception>(() => store.SetMeta("k", "v"));
            }
        }
    }
}
