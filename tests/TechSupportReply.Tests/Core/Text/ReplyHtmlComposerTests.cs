using TechSupportReply.Core.Text;
using Xunit;

namespace TechSupportReply.Tests.Core.Text
{
    public class ReplyHtmlComposerTests
    {
        [Fact]
        public void ToHtml_EncodesSpecialCharacters()
        {
            var html = ReplyHtmlComposer.ToHtml("a < b & \"c\" <script>");
            Assert.Contains("a &lt; b &amp; &quot;c&quot; &lt;script&gt;", html);
            Assert.DoesNotContain("<script>", html);
        }

        [Fact]
        public void ToHtml_OneParagraphPerLine_BlankLinesKept()
        {
            var html = ReplyHtmlComposer.ToHtml("첫 줄\r\n\r\n셋째 줄\n");
            Assert.Contains(">첫 줄</p>", html);
            Assert.Contains(">&nbsp;</p>", html);
            Assert.Contains(">셋째 줄</p>", html);
            Assert.StartsWith("<div id=\"tsr-reply\"", html);
        }

        [Fact]
        public void ToHtml_PreservesLeadingSpaces()
        {
            Assert.Contains(">&nbsp;&nbsp;*CONTROL_TERMINATION</p>", ReplyHtmlComposer.ToHtml("  *CONTROL_TERMINATION"));
        }

        [Fact]
        public void InsertAtTop_AfterWordBodyTag_KeepsSignatureAndQuote()
        {
            var existing = "<html><head><style>p{}</style></head><body lang=KO link=\"#0563C1\" style='word-wrap:break-word'><div class=WordSection1><p>서명</p><div>-----Original Message-----</div></div></body></html>";
            var result = ReplyHtmlComposer.InsertAtTop(existing, "답변");
            var bodyEnd = result.IndexOf("style='word-wrap:break-word'>") + "style='word-wrap:break-word'>".Length;
            Assert.Equal(bodyEnd, result.IndexOf("<div id=\"tsr-reply\""));
            Assert.True(result.IndexOf(">답변</p>") < result.IndexOf("<p>서명</p>"));
            Assert.Contains("-----Original Message-----", result);
            Assert.EndsWith("</body></html>", result);
        }

        [Fact]
        public void InsertAtTop_BodyTagCaseInsensitive()
        {
            var result = ReplyHtmlComposer.InsertAtTop("<HTML><BODY>old</BODY></HTML>", "new");
            Assert.True(result.IndexOf(">new</p>") < result.IndexOf("old"));
        }

        [Fact]
        public void InsertAtTop_NoBody_Prepends()
        {
            Assert.StartsWith("<div id=\"tsr-reply\"", ReplyHtmlComposer.InsertAtTop("<p>old</p>", "new"));
        }

        [Fact]
        public void InsertAtTop_EmptyHtml_CreatesDocument()
        {
            var result = ReplyHtmlComposer.InsertAtTop("", "new");
            Assert.StartsWith("<html><body>", result);
            Assert.Contains(">new</p>", result);
        }
    }
}
