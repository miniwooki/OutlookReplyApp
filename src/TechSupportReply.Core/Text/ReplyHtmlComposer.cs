using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TechSupportReply.Core.Text
{
    /// <summary>답변 텍스트를 Outlook HTML 문단으로 바꾸고, 회신 본문 맨 앞(body 태그 직후)에 넣는다. 서명·인용은 그대로 둔다.</summary>
    public static class ReplyHtmlComposer
    {
        public const string BlockId = "tsr-reply";
        private const string ParagraphOpen = "<p class=MsoNormal style=\"margin:0\">";
        private static readonly Regex BodyTag = new Regex(@"<body\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string ToHtml(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
            var sb = new StringBuilder();
            sb.Append("<div id=\"").Append(BlockId).Append("\" style=\"font-family:'맑은 고딕','Malgun Gothic',sans-serif;font-size:10pt\">");
            foreach (var line in lines)
            {
                sb.Append(ParagraphOpen);
                sb.Append(line.Trim().Length == 0 ? "&nbsp;" : EncodeLine(line));
                sb.Append("</p>");
            }
            sb.Append(ParagraphOpen).Append("&nbsp;</p></div>");
            return sb.ToString();
        }

        public static string InsertAtTop(string existingHtml, string replyText)
        {
            var block = ToHtml(replyText);
            if (string.IsNullOrWhiteSpace(existingHtml)) return "<html><body>" + block + "</body></html>";
            var m = BodyTag.Match(existingHtml);
            return m.Success ? existingHtml.Insert(m.Index + m.Length, block) : block + existingHtml;
        }

        private static string EncodeLine(string line)
        {
            int lead = 0;
            while (lead < line.Length && line[lead] == ' ') lead++;
            var sb = new StringBuilder();
            for (int i = 0; i < lead; i++) sb.Append("&nbsp;");
            sb.Append(WebUtility.HtmlEncode(line.Substring(lead)));
            return sb.ToString();
        }
    }
}
