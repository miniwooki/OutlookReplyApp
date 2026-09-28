using System.IO;
using TechSupportReply.Rag.Loaders;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Loaders
{
    public class EmailLoaderTests
    {
        private static string Doc(string name) => TestPaths.Fixture("docs", name);

        [Fact]
        public void HtmlText_StripsTagsAndDecodesEntities()
        {
            var text = HtmlText.ToPlainText("<html><head><style>p{color:red}</style></head><body><p>a &amp; b</p><script>alert(1)</script>c<br>d &lt;x&gt;</body></html>");
            Assert.Equal("a & b\nc\nd <x>", text);
        }

        [Fact]
        public void Eml_PlainText_SplitsQuestionAndAnswer()
        {
            var mail = MailFileReader.Read(Doc("reply.eml"));
            Assert.Equal("RE: 접촉 관통 문의", mail.Subject);
            Assert.Contains("support@kostech.example", mail.From);
            Assert.NotNull(mail.Date);

            var doc = new EmailLoader().Load(Doc("reply.eml"));
            Assert.Equal(DocKind.Email, doc.Kind);
            var s = Assert.Single(doc.Sections);
            Assert.Equal("RE: 접촉 관통 문의", s.Title);
            Assert.StartsWith("질문:\nLS-DYNA 해석에서 초기 관통 경고가 많이 나옵니다. 어떻게 해야 하나요?\n\n답변:\n안녕하세요, KOSTECH 기술지원팀입니다.", s.Text);
            Assert.Contains("SOFT=2 또는 IGNORE=1", s.Text);
        }

        [Fact]
        public void Eml_HtmlOnly_ConvertsToText()
        {
            var s = Assert.Single(new EmailLoader().Load(Doc("reply_html.eml")).Sections);
            Assert.Contains("질문:\n계산이 발산합니다.", s.Text);
            Assert.Contains("under-relaxation", s.Text);
            Assert.Contains("감사합니다 & 좋은 하루 되세요.", s.Text);
            Assert.DoesNotContain("<p>", s.Text);
            Assert.DoesNotContain("color:red", s.Text);
        }

        [Fact]
        public void Eml_RemovesOlderThreadAndSignature()
        {
            var s = Assert.Single(new EmailLoader().Load(Doc("reply.eml")).Sections);
            Assert.DoesNotContain("더 오래된", s.Text);
            Assert.DoesNotContain("홍길동 / KOSTECH", s.Text);
            Assert.DoesNotContain("From:", s.Text);
        }

        [SkippableFact]
        public void Msg_ReadsSubjectAndBody()
        {
            var path = Doc("reply.msg");
            Skip.IfNot(File.Exists(path), "reply.msg 픽스처가 없습니다(tools/make_msg.ps1로 생성).");
            var mail = MailFileReader.Read(path);
            Assert.False(string.IsNullOrWhiteSpace(mail.Subject));
            Assert.False(string.IsNullOrWhiteSpace(mail.Body));
            Assert.Single(new EmailLoader().Load(path).Sections);
        }
    }
}
