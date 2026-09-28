using System;
using System.IO;
using System.Linq;
using System.Threading;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Loaders;
using TechSupportReply.Rag.Store;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Indexing
{
    public class KbLayoutTests
    {
        [Theory]
        [InlineData(@"replies\a.eml", DocType.Reply)]
        [InlineData("Replies/x.msg", DocType.Reply)]
        [InlineData(@"manuals\a.pdf", DocType.Reference)]
        [InlineData(@"faq\replies.md", DocType.Reference)]
        public void KbLayout_DocTypeFor_Replies(string path, DocType expected)
        {
            Assert.Equal(expected, KbLayout.DocTypeFor(path));
        }

        [Theory]
        [InlineData("_prompt.md", true)]
        [InlineData(@"faq\~$doc.docx", true)]
        [InlineData(".hidden.md", true)]
        [InlineData(@"_drafts\a.md", true)]
        [InlineData(@"manuals\_old\a.pdf", true)]
        [InlineData(@"faq\a.md", false)]
        [InlineData(@"faq\a_b.md", false)]
        public void KbLayout_IsIgnored(string path, bool expected)
        {
            Assert.Equal(expected, KbLayout.IsIgnored(path));
        }
    }

    public class IndexBuilderTests
    {
        private static IndexBuilder Builder(FakeEmbedder embedder) =>
            new IndexBuilder(DocumentLoaderRegistry.CreateDefault(), new Chunker(), embedder);

        private static IndexBuildReport Build(IndexBuilder builder, string productDir, string indexPath, bool full = false) =>
            builder.Build("ls-dyna", productDir, indexPath, full, null, CancellationToken.None);

        private static string Product(TempDir tmp)
        {
            var dir = tmp.Sub("kb/LS-DYNA");
            tmp.File("kb/LS-DYNA/faq/a.md", "# 접촉\n초기 관통 경고는 IGNORE=1로 무시합니다.");
            tmp.File("kb/LS-DYNA/_prompt.md", "지침");
            tmp.File("kb/LS-DYNA/images/x.png", "not an image");
            Directory.CreateDirectory(Path.Combine(dir, "replies"));
            File.Copy(TestPaths.Fixture("docs", "reply.eml"), Path.Combine(dir, "replies", "r.eml"));
            Directory.CreateDirectory(Path.Combine(dir, "manuals"));
            File.Copy(TestPaths.Fixture("docs", "manual.pdf"), Path.Combine(dir, "manuals", "manual.pdf"));
            return dir;
        }

        [Fact]
        public void Build_IndexesSupportedFiles_WithDocTypes()
        {
            using (var tmp = new TempDir())
            {
                var dir = Product(tmp);
                var indexPath = Path.Combine(tmp.Root, "work", "ls-dyna.sqlite");
                var report = Build(Builder(new FakeEmbedder()), dir, indexPath);

                Assert.Equal("ls-dyna", report.ProductId);
                Assert.Equal(3, report.Added);
                Assert.Equal(3, report.FileCount);
                Assert.Empty(report.Skipped);
                using (var store = SqliteIndexStore.OpenReadOnly(indexPath))
                {
                    Assert.Equal(report.ChunkCount, store.ChunkCount);
                    Assert.Equal(FakeEmbedder.Id, store.GetMeta("embedding_model"));
                    Assert.Equal("64", store.GetMeta("dimension"));
                    Assert.Equal("ls-dyna", store.GetMeta("product_id"));
                    Assert.NotNull(store.GetMeta("built_at"));
                    Assert.Single(store.SearchKeyword("IGNORE", 10, DocType.Reply));
                    Assert.Equal(2, store.SearchKeyword("IGNORE", 10, DocType.Reference).Count);
                    Assert.Equal(new[] { @"faq\a.md", @"manuals\manual.pdf", @"replies\r.eml" },
                        store.GetFiles().Select(f => f.RelativePath).ToArray());
                }
            }
        }

        [Fact]
        public void Build_Incremental_UnchangedSkipped_ChangedUpdated_DeletedRemoved()
        {
            using (var tmp = new TempDir())
            {
                var dir = Product(tmp);
                var indexPath = Path.Combine(tmp.Root, "work", "i.sqlite");
                var builder = Builder(new FakeEmbedder());
                Build(builder, dir, indexPath);

                tmp.File("kb/LS-DYNA/faq/a.md", "# 접촉\n내용이 바뀌었습니다.");
                File.SetLastWriteTimeUtc(Path.Combine(dir, "faq", "a.md"), DateTime.UtcNow.AddMinutes(1));
                File.Delete(Path.Combine(dir, "manuals", "manual.pdf"));
                tmp.File("kb/LS-DYNA/faq/b.md", "새 문서");

                var report = Build(builder, dir, indexPath);
                Assert.Equal(1, report.Added);
                Assert.Equal(1, report.Updated);
                Assert.Equal(1, report.Removed);
                Assert.Equal(1, report.Unchanged);
                Assert.Equal(3, report.FileCount);
                using (var store = SqliteIndexStore.OpenReadOnly(indexPath))
                {
                    Assert.Single(store.SearchKeyword("바뀌었습니다", 10));
                    Assert.Empty(store.SearchKeyword("세그먼트", 10));
                }
            }
        }

        [Fact]
        public void Build_SameContentNewMtime_CountsUnchanged()
        {
            using (var tmp = new TempDir())
            {
                var dir = Product(tmp);
                var indexPath = Path.Combine(tmp.Root, "work", "i.sqlite");
                var embedder = new FakeEmbedder();
                var builder = Builder(embedder);
                Build(builder, dir, indexPath);
                int calls = embedder.Calls;

                File.SetLastWriteTimeUtc(Path.Combine(dir, "faq", "a.md"), DateTime.UtcNow.AddHours(1));
                var report = Build(builder, dir, indexPath);
                Assert.Equal(3, report.Unchanged);
                Assert.Equal(0, report.Updated);
                Assert.Equal(calls, embedder.Calls);
            }
        }

        [Fact]
        public void Build_CorruptFiles_SkippedWithReasonOthersIndexed()
        {
            using (var tmp = new TempDir())
            {
                var dir = tmp.Sub("kb/P");
                tmp.File("kb/P/faq/good.md", "정상 문서");
                Directory.CreateDirectory(Path.Combine(dir, "manuals"));
                File.Copy(TestPaths.Fixture("docs", "corrupt.pdf"), Path.Combine(dir, "manuals", "corrupt.pdf"));
                File.Copy(TestPaths.Fixture("docs", "encrypted.pdf"), Path.Combine(dir, "manuals", "encrypted.pdf"));

                var report = Build(Builder(new FakeEmbedder()), dir, Path.Combine(tmp.Root, "i.sqlite"));
                Assert.Equal(1, report.Added);
                Assert.Equal(2, report.Skipped.Count);
                Assert.Contains(report.Skipped, s => s.RelativePath == @"manuals\corrupt.pdf" && s.Reason.Contains("PDF를 읽을 수 없습니다"));
                Assert.Contains(report.Skipped, s => s.RelativePath == @"manuals\encrypted.pdf" && s.Reason.Contains("암호"));
            }
        }

        [Fact]
        public void Build_ModelChanged_RebuildsAll()
        {
            using (var tmp = new TempDir())
            {
                var dir = Product(tmp);
                var indexPath = Path.Combine(tmp.Root, "work", "i.sqlite");
                var builder = Builder(new FakeEmbedder());
                Build(builder, dir, indexPath);
                using (var store = SqliteIndexStore.Create(indexPath)) store.SetMeta("embedding_model", "old-model");

                var report = Build(builder, dir, indexPath);
                Assert.Equal(3, report.Added);
                Assert.Equal(0, report.Unchanged);
                using (var store = SqliteIndexStore.OpenReadOnly(indexPath))
                    Assert.Equal(FakeEmbedder.Id, store.GetMeta("embedding_model"));
            }
        }

        [Fact]
        public void Build_MissingFolder_EmptyIndex()
        {
            using (var tmp = new TempDir())
            {
                var indexPath = Path.Combine(tmp.Root, "i.sqlite");
                var report = Build(Builder(new FakeEmbedder()), Path.Combine(tmp.Root, "nope"), indexPath);
                Assert.Equal(0, report.FileCount);
                Assert.True(File.Exists(indexPath));
            }
        }

        [Fact]
        public void Build_NoChanges_KeepsBuiltAtAndReportsNoChanges()
        {
            using (var tmp = new TempDir())
            {
                var dir = Product(tmp);
                var indexPath = Path.Combine(tmp.Root, "work", "i.sqlite");
                var builder = Builder(new FakeEmbedder());
                Assert.True(Build(builder, dir, indexPath).HasChanges);
                string builtAt;
                using (var store = SqliteIndexStore.OpenReadOnly(indexPath)) builtAt = store.GetMeta("built_at");
                Thread.Sleep(20);
                Assert.False(Build(builder, dir, indexPath).HasChanges);
                using (var store = SqliteIndexStore.OpenReadOnly(indexPath)) Assert.Equal(builtAt, store.GetMeta("built_at"));
            }
        }

        [Fact]
        public void Build_UnsupportedExtensions_AreCounted()
        {
            using (var tmp = new TempDir())
            {
                var dir = Product(tmp);
                tmp.File("kb/LS-DYNA/manuals/old.hwp", "x");
                tmp.File("kb/LS-DYNA/manuals/slides.PPTX", "x");
                tmp.File("kb/LS-DYNA/manuals/more.hwp", "x");
                var report = Build(Builder(new FakeEmbedder()), dir, Path.Combine(tmp.Root, "i.sqlite"));
                Assert.Equal(2, report.Unsupported[".hwp"]);
                Assert.Equal(1, report.Unsupported[".pptx"]);
                Assert.Equal(1, report.Unsupported[".png"]);
                Assert.False(report.Unsupported.ContainsKey(".md"));
            }
        }
    }
}
