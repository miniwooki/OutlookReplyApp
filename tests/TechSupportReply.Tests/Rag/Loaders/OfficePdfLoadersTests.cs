using System.Linq;
using TechSupportReply.Rag.Loaders;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Loaders
{
    public class OfficePdfLoadersTests
    {
        private static string Doc(string name) => TestPaths.Fixture("docs", name);

        [Fact]
        public void PdfLoader_ReadsPagesWithPageNumbers()
        {
            var doc = new PdfLoader().Load(Doc("manual.pdf"));
            Assert.Equal(DocKind.Pdf, doc.Kind);
            Assert.Equal(new int?[] { 1, 2 }, doc.Sections.Select(s => s.Page).ToArray());
            Assert.Contains("*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE", doc.Sections[0].Text);
            Assert.Contains("세그먼트 기반 접촉", doc.Sections[0].Text);
            Assert.Contains("IGNORE=1", doc.Sections[1].Text);
        }

        [Fact]
        public void PdfLoader_Encrypted_ThrowsWithReason()
        {
            var ex = Assert.Throws<DocumentLoadException>(() => new PdfLoader().Load(Doc("encrypted.pdf")));
            Assert.Contains("암호", ex.Reason);
        }

        [Fact]
        public void PdfLoader_Scanned_ThrowsNoText()
        {
            var ex = Assert.Throws<DocumentLoadException>(() => new PdfLoader().Load(Doc("scanned.pdf")));
            Assert.Contains("텍스트가 없는", ex.Reason);
        }

        [Fact]
        public void PdfLoader_Corrupt_ThrowsReadable()
        {
            var ex = Assert.Throws<DocumentLoadException>(() => new PdfLoader().Load(Doc("corrupt.pdf")));
            Assert.Contains("PDF를 읽을 수 없습니다", ex.Reason);
        }

        [Fact]
        public void DocxLoader_SplitsByHeadingAndReadsTables()
        {
            var doc = new DocxLoader().Load(Doc("guide.docx"));
            Assert.Equal(DocKind.Word, doc.Kind);
            Assert.Equal(new[] { "guide", "질량 스케일링", "접촉 설정" }, doc.Sections.Select(s => s.Title).ToArray());
            Assert.Contains("DT2MS", doc.Sections[1].Text);
            Assert.Contains("SOFT=2 | 세그먼트 기반", doc.Sections[2].Text);
        }

        [Fact]
        public void XlsxLoader_RowsBecomeSections()
        {
            var doc = new XlsxLoader().Load(Doc("issues.xlsx"));
            Assert.Equal(DocKind.Table, doc.Kind);
            Assert.Equal(new[] { "이슈 #1", "이슈 #2", "버전 #1" }, doc.Sections.Select(s => s.Title).ToArray());
            Assert.Equal("증상: 음수 부피\n원인: 요소 왜곡\n조치: 메시 개선", doc.Sections[0].Text);
            Assert.Equal("증상: 초기 관통\n조치: IGNORE=1", doc.Sections[1].Text);
            Assert.Equal("버전: 13.1\n비고: MPP 권장", doc.Sections[2].Text);
        }

        [Fact]
        public void Registry_Default_SupportsOfficeAndPdf()
        {
            var registry = DocumentLoaderRegistry.CreateDefault();
            Assert.True(registry.CanLoad("a.pdf"));
            Assert.True(registry.CanLoad("a.docx"));
            Assert.True(registry.CanLoad("a.xlsx"));
        }
    }
}
