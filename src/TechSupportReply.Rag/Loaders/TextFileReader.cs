using System.IO;
using System.Text;

namespace TechSupportReply.Rag.Loaders
{
    /// <summary>BOM → 엄격한 UTF-8 → CP949(한국어 ANSI) 순으로 인코딩을 판별해 텍스트를 읽는다.</summary>
    public static class TextFileReader
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static bool _codePagesRegistered;

        public static void EnsureCodePages()
        {
            if (_codePagesRegistered) return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _codePagesRegistered = true;
        }

        public static string ReadAllText(string path) => Decode(File.ReadAllBytes(path));

        public static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                EnsureCodePages();
                return Encoding.GetEncoding(949).GetString(bytes);
            }
        }
    }
}
