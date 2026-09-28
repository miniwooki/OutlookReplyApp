using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TechSupportReply.Rag.Indexing
{
    public static class FileHash
    {
        /// <summary>파일 내용의 SHA-256(소문자 16진수).</summary>
        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
        }
    }
}
