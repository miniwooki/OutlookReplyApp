using TechSupportReply.Core.Knowledge;
using Xunit;

namespace TechSupportReply.Tests.Core.Knowledge
{
    public class KnowledgeChunkTests
    {
        [Fact]
        public void Citation_WithPage()
        {
            var c = new KnowledgeChunk { SourceFile = @"manuals\Keyword_Vol_I.pdf", Page = 12 };
            Assert.Equal("Keyword_Vol_I.pdf p.12", c.Citation);
        }

        [Fact]
        public void Citation_WithoutPage()
        {
            Assert.Equal("faq.md", new KnowledgeChunk { SourceFile = @"faq\faq.md" }.Citation);
        }

        [Fact]
        public void Citation_MissingSource()
        {
            Assert.Equal("(출처 미상)", new KnowledgeChunk().Citation);
        }
    }
}
