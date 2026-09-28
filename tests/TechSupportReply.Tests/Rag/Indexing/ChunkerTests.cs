using System.Linq;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Loaders;
using Xunit;

namespace TechSupportReply.Tests.Rag.Indexing
{
    public class ChunkerTests
    {
        private static LoadedDocument Doc(DocKind kind, params LoadedSection[] sections) =>
            new LoadedDocument { Path = "x", Kind = kind, Title = "doc", Sections = sections.ToList() };

        private static LoadedSection Section(string text, string title = "t", int? page = null) =>
            new LoadedSection { Title = title, Page = page, Text = text };

        [Fact]
        public void ShortSection_OneChunk()
        {
            var chunks = new Chunker(100, 20).Chunk(Doc(DocKind.Text, Section("짧은 본문")));
            var c = Assert.Single(chunks);
            Assert.Equal("짧은 본문", c.Text);
        }

        [Fact]
        public void EmptySections_Skipped()
        {
            Assert.Empty(new Chunker(100, 20).Chunk(Doc(DocKind.Text, Section("  \n "), Section(""))));
        }

        [Fact]
        public void LongSection_SplitsWithinMaxAndOverlaps()
        {
            var text = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"문단 {i:00}: " + new string('가', 30)));
            var chunks = new Chunker(200, 40).Chunk(Doc(DocKind.Pdf, Section(text)));
            Assert.True(chunks.Count > 1);
            Assert.All(chunks, c => Assert.True(c.Text.Length <= 200, $"길이 {c.Text.Length}"));
            for (int i = 1; i < chunks.Count; i++)
            {
                var tail = chunks[i - 1].Text.Substring(chunks[i - 1].Text.Length - 10);
                Assert.Contains(tail, chunks[i].Text);
            }
            Assert.Contains("문단 01", chunks.First().Text);
            Assert.Contains("문단 30", chunks.Last().Text);
        }

        [Fact]
        public void SplitPrefersParagraphBoundary()
        {
            var text = new string('a', 80) + "\n\n" + new string('b', 80);
            var chunks = new Chunker(120, 0).Chunk(Doc(DocKind.Text, Section(text)));
            Assert.Equal(2, chunks.Count);
            Assert.Equal(new string('a', 80), chunks[0].Text);
            Assert.Equal(new string('b', 80), chunks[1].Text);
        }

        [Fact]
        public void PageAndTitle_Preserved()
        {
            var chunks = new Chunker(50, 10).Chunk(Doc(DocKind.Pdf, Section(string.Join(" ", Enumerable.Repeat("단어", 60)), "매뉴얼", 12)));
            Assert.True(chunks.Count > 1);
            Assert.All(chunks, c =>
            {
                Assert.Equal("매뉴얼", c.Title);
                Assert.Equal(12, c.Page);
            });
        }

        [Fact]
        public void EmailSection_SingleChunkEvenIfLong()
        {
            var text = "질문:\n" + new string('q', 3000) + "\n\n답변:\n" + new string('a', 3000);
            var c = Assert.Single(new Chunker(1500, 200).Chunk(Doc(DocKind.Email, Section(text))));
            Assert.Contains("[... 이하 ", c.Text);
            Assert.True(c.Text.Length < 4100);
        }

        [Fact]
        public void NoParagraphs_HardSplitsByLength()
        {
            var chunks = new Chunker(100, 10).Chunk(Doc(DocKind.Text, Section(new string('x', 350))));
            Assert.All(chunks, c => Assert.True(c.Text.Length <= 100));
            Assert.True(chunks.Count >= 4);
        }
    }
}
