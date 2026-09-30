using System;
using System.IO;
using TechSupportReply.App.Hosting;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class NativeLibraryPreloaderTests
    {
        [Fact]
        public void Preload_FromTestOutput_LoadsOnnxRuntimeFromThatFolder()
        {
            var dir = AppContext.BaseDirectory;
            var warnings = NativeLibraryPreloader.Preload(dir);
            Assert.Empty(warnings);
            var loaded = NativeLibraryPreloader.LoadedModulePath("onnxruntime.dll");
            Assert.StartsWith(Path.GetFullPath(dir).TrimEnd('\\'), loaded, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Preload_MissingFolder_ReturnsWarningPerLibrary()
        {
            using (var tmp = new TempDir())
            {
                var warnings = NativeLibraryPreloader.Preload(tmp.Root);
                Assert.Equal(NativeLibraryPreloader.Libraries.Length, warnings.Count);
                Assert.Contains(warnings, w => w.Contains("onnxruntime.dll"));
            }
        }

        [Fact]
        public void AssemblyDirectory_IsTestOutput()
        {
            Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'),
                NativeLibraryPreloader.AssemblyDirectory(typeof(NativeLibraryPreloader).Assembly).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);
        }
    }
}
