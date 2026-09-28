using System.IO;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests
{
    public class SmokeTests
    {
        [Fact]
        public void RepoRoot_ContainsSolution()
        {
            Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, "TechSupportReply.sln")));
        }

        [Fact]
        public void Process_Is64Bit()
        {
            Assert.True(System.Environment.Is64BitProcess, "테스트는 x64로 실행되어야 합니다(ONNX/SQLite 네이티브 DLL).");
        }
    }
}
