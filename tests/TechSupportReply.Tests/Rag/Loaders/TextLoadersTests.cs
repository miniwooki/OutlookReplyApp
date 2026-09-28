using System.IO;
using System.Linq;
using System.Text;
using TechSupportReply.Rag.Loaders;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Loaders
{
    public class TextLoadersTests
    {
        [Fact]
        public void TextLoader_SingleSectionWithFileTitle()
        {
            using (var tmp = new TempDir())
            {
                var doc = new TextLoader().Load(tmp.File("faq/timestep.txt", "시간 간격 문의\n답변 내용"));
                Assert.Equal(DocKind.Text, doc.Kind);
                Assert.Equal("timestep", doc.Title);
                var s = Assert.Single(doc.Sections);
                Assert.Equal("timestep", s.Title);
                Assert.Equal("시간 간격 문의\n답변 내용", s.Text);
                Assert.Null(s.Page);
            }
        }

        [Fact]
        public void MarkdownLoader_SplitsByHeadings()
        {
            using (var tmp = new TempDir())
            {
                var md = "머리말 문단\n\n# 접촉\n접촉 설명\n\n## SOFT 옵션\nSOFT=2 설명\n\n# 재료\n재료 설명";
                var doc = new MarkdownLoader().Load(tmp.File("faq/contact.md", md));
                Assert.Equal(DocKind.Markdown, doc.Kind);
                Assert.Equal(new[] { "contact", "접촉", "SOFT 옵션", "재료" }, doc.Sections.Select(s => s.Title).ToArray());
                Assert.Equal("머리말 문단", doc.Sections[0].Text);
                Assert.Equal("SOFT=2 설명", doc.Sections[2].Text);
            }
        }

        [Fact]
        public void MarkdownLoader_NoHeadings_SingleSection()
        {
            using (var tmp = new TempDir())
            {
                var doc = new MarkdownLoader().Load(tmp.File("a.md", "그냥 본문\n둘째 줄"));
                var s = Assert.Single(doc.Sections);
                Assert.Equal("a", s.Title);
                Assert.Equal("그냥 본문\n둘째 줄", s.Text);
            }
        }

        [Fact]
        public void CsvParser_QuotedFieldsWithCommaAndNewline()
        {
            var rows = CsvParser.Parse("id,내용\r\n1,\"쉼표, 포함\"\r\n2,\"줄\n바꿈 \"\"따옴표\"\"\"\r\n");
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "1", "쉼표, 포함" }, rows[1]);
            Assert.Equal(new[] { "2", "줄\n바꿈 \"따옴표\"" }, rows[2]);
        }

        [Fact]
        public void CsvParser_DetectsTabDelimiter()
        {
            var rows = CsvParser.Parse("a\tb\n1,5\t2");
            Assert.Equal(new[] { "1,5", "2" }, rows[1]);
        }

        [Fact]
        public void CsvLoader_RowsBecomeHeaderValueSections()
        {
            using (var tmp = new TempDir())
            {
                var doc = new CsvLoader().Load(tmp.File("issues/issues.csv", "증상,원인,조치\n음수 부피,요소 왜곡,,\n\n초기 관통,,간격 조정\n"));
                Assert.Equal(DocKind.Table, doc.Kind);
                Assert.Equal(2, doc.Sections.Count);
                Assert.Equal("issues #1", doc.Sections[0].Title);
                Assert.Equal("증상: 음수 부피\n원인: 요소 왜곡", doc.Sections[0].Text);
                Assert.Equal("issues #3", doc.Sections[1].Title);
                Assert.Equal("증상: 초기 관통\n조치: 간격 조정", doc.Sections[1].Text);
            }
        }

        [Fact]
        public void CsvLoader_Cp949WithQuotedFields()
        {
            TextFileReader.EnsureCodePages();
            using (var tmp = new TempDir())
            {
                var path = tmp.File("issues/cp949.csv");
                File.WriteAllBytes(path, Encoding.GetEncoding(949).GetBytes("증상,조치\r\n\"수렴 실패, 발산\",\"완화 계수 조정\"\r\n"));
                var s = Assert.Single(new CsvLoader().Load(path).Sections);
                Assert.Equal("증상: 수렴 실패, 발산\n조치: 완화 계수 조정", s.Text);
            }
        }

        [Fact]
        public void CsvLoader_HeaderOnly_NoSections()
        {
            using (var tmp = new TempDir())
            {
                Assert.Empty(new CsvLoader().Load(tmp.File("h.csv", "a,b\n")).Sections);
            }
        }
    }
}
