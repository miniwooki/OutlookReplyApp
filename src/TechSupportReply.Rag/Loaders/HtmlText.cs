using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace TechSupportReply.Rag.Loaders
{
    /// <summary>메일 HTML 본문을 일반 텍스트로 바꾼다.</summary>
    public static class HtmlText
    {
        private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        private static readonly Regex Hidden = new Regex(@"<(script|style|head)\b.*?</\1\s*>|<!--.*?-->", Opt);
        private static readonly Regex LineBreaks = new Regex(@"<br\s*/?>|</(p|div|tr|li|h[1-6]|table|blockquote)\s*>", Opt);
        private static readonly Regex Tags = new Regex(@"<[^>]+>", Opt);
        private static readonly Regex Spaces = new Regex(@"[ \t ]+");
        private static readonly Regex BlankLines = new Regex(@"\n{3,}");

        public static string ToPlainText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var s = Hidden.Replace(html.Replace("\r\n", "\n"), "");
            s = s.Replace("\n", " ");
            s = LineBreaks.Replace(s, "\n");
            s = Tags.Replace(s, "");
            s = WebUtility.HtmlDecode(s);
            s = Spaces.Replace(s, " ");
            s = string.Join("\n", s.Split('\n').Select(l => l.Trim()));
            return BlankLines.Replace(s, "\n\n").Trim();
        }
    }
}
