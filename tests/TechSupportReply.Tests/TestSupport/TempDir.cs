using System;
using System.IO;
using System.Text;

namespace TechSupportReply.Tests.TestSupport
{
    internal sealed class TempDir : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "tsr-tests", Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(Root);
        }

        /// <summary>상대 경로 파일의 전체 경로를 만들고, content가 있으면 UTF-8(BOM 없음)로 쓴다.</summary>
        public string File(string relative, string content = null)
        {
            var full = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            if (content != null) System.IO.File.WriteAllText(full, content, new UTF8Encoding(false));
            return full;
        }

        public string Sub(string relative)
        {
            var full = Path.Combine(Root, relative);
            Directory.CreateDirectory(full);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
