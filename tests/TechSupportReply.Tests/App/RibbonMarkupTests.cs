using System.Linq;
using System.Xml.Linq;
using TechSupportReply.App.Hosting;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class RibbonMarkupTests
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/office/2009/07/customui";

        [Theory]
        [InlineData(RibbonMarkup.ExplorerRibbonId, "TabMail")]
        [InlineData(RibbonMarkup.ReadMailRibbonId, "TabReadMessage")]
        public void Get_ReturnsWellFormedXml_ForSupportedRibbons(string ribbonId, string tab)
        {
            var doc = XDocument.Parse(RibbonMarkup.Get(ribbonId));
            Assert.Equal("OnRibbonLoad", (string)doc.Root.Attribute("onLoad"));
            Assert.Equal(tab, (string)doc.Descendants(Ns + "tab").Single().Attribute("idMso"));
            var actions = doc.Descendants(Ns + "button").Select(b => (string)b.Attribute("onAction")).ToList();
            Assert.Equal(new[] { "OnReplyClick", "OnSettingsClick" }, actions);
            var ids = doc.Descendants().Select(e => (string)e.Attribute("id")).Where(i => i != null).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }

        [Theory]
        [InlineData("Microsoft.Outlook.Mail.Compose")]
        [InlineData("")]
        [InlineData(null)]
        public void Get_ReturnsNull_ForOtherRibbons(string ribbonId)
        {
            Assert.Null(RibbonMarkup.Get(ribbonId));
        }
    }
}
