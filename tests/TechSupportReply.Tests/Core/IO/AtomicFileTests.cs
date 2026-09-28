using System.IO;
using TechSupportReply.Core.IO;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.IO
{
    public class AtomicFileTests
    {
        [Fact]
        public void WriteAllText_CreatesDirectoryAndOverwrites()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Root, "a", "b", "file.json");
                AtomicFile.WriteAllText(path, "첫번째");
                AtomicFile.WriteAllText(path, "두번째");
                Assert.Equal("두번째", File.ReadAllText(path));
                Assert.False(File.Exists(path + ".tmp"));
            }
        }
    }
}
