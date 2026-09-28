using TechSupportReply.Rag.Search;
using Xunit;

namespace TechSupportReply.Tests.Rag.Search
{
    public class SearchTextNormalizerTests
    {
        [Fact]
        public void Terms_KeywordCard_KeepsFullTokenAndParts()
        {
            var terms = SearchTextNormalizer.Terms("*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE");
            Assert.Contains("contact_automatic_surface_to_surface", terms);
            Assert.Contains("contact", terms);
            Assert.Contains("automatic", terms);
            Assert.Contains("surface", terms);
        }

        [Fact]
        public void Terms_KoreanTwoSyllable()
        {
            Assert.Equal(new[] { "접촉", "관통" }, SearchTextNormalizer.Terms("접촉 관통"));
            Assert.Equal(new[] { "수렴" }, SearchTextNormalizer.Terms("수렴"));
        }

        [Fact]
        public void Terms_KoreanWithParticle_ProducesBigrams()
        {
            Assert.Equal(new[] { "접촉", "촉이" }, SearchTextNormalizer.Terms("접촉이"));
        }

        [Fact]
        public void Terms_FullWidth_IsNormalized()
        {
            Assert.Equal(new[] { "ls", "dyna" }, SearchTextNormalizer.Terms("ＬＳ－ＤＹＮＡ"));
        }

        [Fact]
        public void Terms_DropsSingleLatinLetters()
        {
            Assert.Equal(new[] { "d3hsp" }, SearchTextNormalizer.Terms("a d3hsp"));
        }

        [Fact]
        public void ToMatchQuery_OnlyStopTerms_ReturnsNull()
        {
            Assert.Null(SearchTextNormalizer.ToMatchQuery("안녕하세요 감사합니다"));
            Assert.Null(SearchTextNormalizer.ToMatchQuery(""));
        }

        [Fact]
        public void ToMatchQuery_QuotesAndOrsDistinctTerms()
        {
            Assert.Equal("\"fluent\" OR \"수렴\"", SearchTextNormalizer.ToMatchQuery("Fluent 수렴 fluent"));
        }

        [Fact]
        public void ToMatchQuery_LimitsTermCount()
        {
            var q = SearchTextNormalizer.ToMatchQuery("aa bb cc dd ee", 2);
            Assert.Equal("\"aa\" OR \"bb\"", q);
        }
    }
}
