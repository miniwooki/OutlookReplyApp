using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TechSupportReply.Core.Text
{
    public sealed class EmailParts
    {
        public string Latest { get; set; } = "";
        public string Quoted { get; set; } = "";
    }

    /// <summary>메일 본문을 최신 작성분과 인용된 이전 스레드로 분리한다.</summary>
    public static class EmailTextCleaner
    {
        private const RegexOptions Ml = RegexOptions.Multiline;
        private const RegexOptions MlIc = RegexOptions.Multiline | RegexOptions.IgnoreCase;

        private static readonly Regex[] SeparatorPatterns =
        {
            new Regex(@"^-{2,}\s*(Original Message|원본 메시지|Forwarded message|전달된 메시지)\s*-{2,}\s*$", MlIc),
            new Regex(@"^_{10,}\s*$", Ml),
            new Regex(@"^(From|보낸 사람|보낸사람)\s*:[^\n]*\n(?:[^\n]*\n){0,2}?(Sent|Date|보낸 날짜|날짜)\s*:", MlIc),
            new Regex(@"^On\s.{5,200}\swrote:\s*$", MlIc),
            new Regex(@"^\d{4}[.\-/]\s?\d{1,2}[.\-/]\s?\d{1,2}.{0,60}작성:\s*$", Ml),
        };

        private static readonly Regex ExcessBlankLines = new Regex(@"\n{3,}");
        private static readonly Regex QuoteMarker = new Regex(@"^[ \t]*(>[ \t]?)+", Ml);

        public static EmailParts Split(string body)
        {
            var text = Normalize(body);
            int cut = text.Length;
            foreach (var pattern in SeparatorPatterns)
            {
                var m = pattern.Match(text);
                if (m.Success && m.Index < cut) cut = m.Index;
            }

            var latestLines = new List<string>();
            var inlineQuoted = new List<string>();
            foreach (var line in text.Substring(0, cut).Split('\n'))
            {
                if (line.TrimStart().StartsWith(">")) inlineQuoted.Add(line);
                else latestLines.Add(line);
            }

            var quoted = string.Join("\n", inlineQuoted.Concat(new[] { text.Substring(cut) }));
            return new EmailParts
            {
                Latest = Tidy(string.Join("\n", latestLines)),
                Quoted = Tidy(QuoteMarker.Replace(quoted, "")),
            };
        }

        private static string Normalize(string body) =>
            (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ');

        private static string Tidy(string text)
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd());
            return ExcessBlankLines.Replace(string.Join("\n", lines), "\n\n").Trim();
        }
    }
}
