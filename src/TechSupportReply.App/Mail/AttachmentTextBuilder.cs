using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TechSupportReply.Rag.Loaders;

namespace TechSupportReply.App.Mail
{
    public sealed class SavedAttachment
    {
        public SavedAttachment(string fileName, string path)
        {
            FileName = fileName ?? "";
            Path = path ?? "";
        }

        public string FileName { get; }
        public string Path { get; }
    }

    /// <summary>
    /// 메일 첨부 중 로그·키워드 파일 같은 텍스트를 골라 프롬프트용 발췌를 만든다.
    /// 긴 파일(d3hsp 등)은 앞부분과 오류가 모이는 뒷부분만 남긴다.
    /// </summary>
    public static class AttachmentTextBuilder
    {
        public const long MaxFileBytes = 20L * 1024 * 1024;
        public const int MaxTotalChars = 20000;
        public const int HeadChars = 3000;
        public const int TailChars = 5000;

        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".csv", ".md", ".k", ".key", ".dyn", ".inp", ".out", ".trn", ".err", ".json", ".xml",
        };
        private static readonly Regex KnownNames = new Regex(@"^(d3hsp|messag|mes\d{4}|glstat)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InlineImage = new Regex(@"^image\d{3}\.(png|jpe?g|gif|bmp)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsInlineImage(string name) => InlineImage.IsMatch(name ?? "");

        public static bool IsTextCandidate(string name, long size)
        {
            if (string.IsNullOrWhiteSpace(name) || size > MaxFileBytes) return false;
            return TextExtensions.Contains(System.IO.Path.GetExtension(name)) || KnownNames.IsMatch(name);
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "attachment";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        public static string Build(IEnumerable<SavedAttachment> files, int maxTotalChars = MaxTotalChars)
        {
            var sb = new StringBuilder();
            foreach (var f in files ?? Enumerable.Empty<SavedAttachment>())
            {
                string text;
                try { text = TextFileReader.ReadAllText(f.Path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }

                var block = "### " + f.FileName + "\n" + Excerpt(text.Replace("\r\n", "\n")).Trim() + "\n\n";
                if (sb.Length + block.Length > maxTotalChars)
                {
                    var remain = maxTotalChars - sb.Length;
                    if (remain > 200) sb.Append(block.Substring(0, remain));
                    sb.Append("\n…(첨부 텍스트 한도 초과로 생략)…");
                    break;
                }
                sb.Append(block);
            }
            return sb.ToString().TrimEnd();
        }

        internal static string Excerpt(string text) =>
            text.Length <= HeadChars + TailChars
                ? text
                : text.Substring(0, HeadChars) + "\n…(중략)…\n" + text.Substring(text.Length - TailChars);
    }
}
