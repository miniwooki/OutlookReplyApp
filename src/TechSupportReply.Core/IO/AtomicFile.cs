using System.IO;
using System.Text;

namespace TechSupportReply.Core.IO
{
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string content) =>
            WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var tmp = full + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            Replace(tmp, full);
        }

        /// <summary>source를 destination으로 교체한다. SMB 등에서 File.Replace가 실패하면 복사 후 삭제로 대체한다.</summary>
        public static void Replace(string source, string destination)
        {
            if (!File.Exists(destination))
            {
                File.Move(source, destination);
                return;
            }
            try
            {
                File.Replace(source, destination, null);
            }
            catch (IOException)
            {
                File.Copy(source, destination, true);
                File.Delete(source);
            }
        }
    }
}
