using System.IO;
using System.Linq;
using System.Text;
using TechSupportReply.App.Mail;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class AttachmentTextBuilderTests
    {
        [Theory]
        [InlineData("d3hsp", 1000, true)]
        [InlineData("messag", 1000, true)]
        [InlineData("mes0003", 1000, true)]
        [InlineData("model.k", 1000, true)]
        [InlineData("run.LOG", 1000, true)]
        [InlineData("fluent.trn", 1000, true)]
        [InlineData("issues.csv", 1000, true)]
        [InlineData("manual.pdf", 1000, false)]
        [InlineData("image001.png", 1000, false)]
        [InlineData("huge.log", 21L * 1024 * 1024, false)]
        public void IsTextCandidate(string name, long size, bool expected)
        {
            Assert.Equal(expected, AttachmentTextBuilder.IsTextCandidate(name, size));
        }

        [Theory]
        [InlineData("image001.png", true)]
        [InlineData("IMAGE012.JPG", true)]
        [InlineData("result.png", false)]
        public void IsInlineImage(string name, bool expected)
        {
            Assert.Equal(expected, AttachmentTextBuilder.IsInlineImage(name));
        }

        [Fact]
        public void SafeFileName_ReplacesInvalidChars()
        {
            Assert.Equal("a_b_c.txt", AttachmentTextBuilder.SafeFileName("a/b:c.txt"));
            Assert.Equal("attachment", AttachmentTextBuilder.SafeFileName(""));
        }

        [Fact]
        public void Build_LongFile_KeepsHeadAndTail()
        {
            using (var tmp = new TempDir())
            {
                var content = "HEAD" + new string('x', 20000) + "TAIL";
                var path = tmp.File("d3hsp", content);
                var text = AttachmentTextBuilder.Build(new[] { new SavedAttachment("d3hsp", path) });
                Assert.StartsWith("### d3hsp\nHEAD", text);
                Assert.EndsWith("TAIL", text);
                Assert.Contains("…(중략)…", text);
                Assert.True(text.Length < AttachmentTextBuilder.HeadChars + AttachmentTextBuilder.TailChars + 100);
            }
        }

        [Fact]
        public void Build_RespectsTotalLimit_AndSkipsMissingFiles()
        {
            using (var tmp = new TempDir())
            {
                var files = Enumerable.Range(0, 5).Select(i => new SavedAttachment($"f{i}.log", tmp.File($"f{i}.log", new string((char)('a' + i), 7000)))).ToList();
                files.Insert(0, new SavedAttachment("gone.log", Path.Combine(tmp.Root, "gone.log")));
                var text = AttachmentTextBuilder.Build(files, maxTotalChars: 15000);
                Assert.True(text.Length <= 15000 + 50);
                Assert.Contains("### f0.log", text);
                Assert.DoesNotContain("gone.log", text);
                Assert.Contains("생략", text);
            }
        }

        [Fact]
        public void Build_ReadsCp949()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Root, "korean.txt");
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                File.WriteAllText(path, "해석이 발산합니다", Encoding.GetEncoding(949));
                Assert.Contains("해석이 발산합니다", AttachmentTextBuilder.Build(new[] { new SavedAttachment("korean.txt", path) }));
            }
        }
    }
}
