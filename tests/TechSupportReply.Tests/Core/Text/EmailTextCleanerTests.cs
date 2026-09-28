using TechSupportReply.Core.Text;
using Xunit;

namespace TechSupportReply.Tests.Core.Text
{
    public class EmailTextCleanerTests
    {
        [Fact]
        public void Split_OutlookEnglishSeparator()
        {
            var body = "Hi,\r\n\r\nPlease check.\r\n\r\n________________________________\r\nFrom: Kim <k@x.com>\r\nSent: Monday, September 1, 2026 10:00 AM\r\nTo: support@x.com\r\nSubject: RE: issue\r\n\r\nOriginal question";
            var parts = EmailTextCleaner.Split(body);
            Assert.Equal("Hi,\n\nPlease check.", parts.Latest);
            Assert.Contains("Original question", parts.Quoted);
        }

        [Fact]
        public void Split_OutlookKoreanHeaders()
        {
            var body = "답변입니다.\n\n보낸 사람: 홍길동 <h@x.com>\n보낸 날짜: 2026년 9월 1일 월요일 오전 10:00\n받는 사람: support@x.com\n제목: 문의\n\n문의 내용";
            var parts = EmailTextCleaner.Split(body);
            Assert.Equal("답변입니다.", parts.Latest);
            Assert.Contains("문의 내용", parts.Quoted);
        }

        [Fact]
        public void Split_GmailWroteLineWithQuoteMarkers()
        {
            var body = "Thanks!\n\nOn Mon, Sep 1, 2026 at 10:00 AM Kim <k@x.com> wrote:\n> Question line\n> second";
            var parts = EmailTextCleaner.Split(body);
            Assert.Equal("Thanks!", parts.Latest);
            Assert.Contains("Question line\nsecond", parts.Quoted);
            Assert.DoesNotContain("> Question", parts.Quoted);
        }

        [Fact]
        public void Split_OriginalMessageMarker()
        {
            var parts = EmailTextCleaner.Split("확인했습니다.\n\n-----Original Message-----\nFrom: a\n이전 내용");
            Assert.Equal("확인했습니다.", parts.Latest);
            Assert.Contains("이전 내용", parts.Quoted);
        }

        [Fact]
        public void Split_InlineQuotedLines_MoveToQuoted()
        {
            var parts = EmailTextCleaner.Split("네 맞습니다.\n> 이렇게 하면 되나요?\n추가 설명");
            Assert.Equal("네 맞습니다.\n추가 설명", parts.Latest);
            Assert.Contains("이렇게 하면 되나요?", parts.Quoted);
        }

        [Fact]
        public void Split_NoQuote_ReturnsWholeTrimmed()
        {
            var parts = EmailTextCleaner.Split("  a\r\n\r\n\r\n\r\nb  ");
            Assert.Equal("a\n\nb", parts.Latest);
            Assert.Equal("", parts.Quoted);
        }

        [Fact]
        public void Split_Null_ReturnsEmpty()
        {
            var parts = EmailTextCleaner.Split(null);
            Assert.Equal("", parts.Latest);
            Assert.Equal("", parts.Quoted);
        }
    }
}
