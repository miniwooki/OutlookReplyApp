using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TechSupportReply.Core.Text;

namespace TechSupportReply.Rag.Loaders
{
    public sealed class MailFileContent
    {
        public string Subject { get; set; } = "";
        public string From { get; set; } = "";
        public DateTime? Date { get; set; }
        public string Body { get; set; } = "";
    }

    /// <summary>.eml(MIME)과 .msg(Outlook) 파일에서 제목·보낸 사람·날짜·본문을 읽는다.</summary>
    public static class MailFileReader
    {
        public static MailFileContent Read(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".msg") return ReadMsg(path);
            if (ext == ".eml") return ReadEml(path);
            throw new DocumentLoadException(path, "메일 파일(.eml/.msg)이 아닙니다.");
        }

        private static MailFileContent ReadEml(string path)
        {
            var message = MsgReader.Mime.Message.Load(new FileInfo(path));
            var from = message.Headers?.From;
            var text = message.TextBody?.GetBodyAsText();
            if (string.IsNullOrWhiteSpace(text)) text = HtmlText.ToPlainText(message.HtmlBody?.GetBodyAsText());
            return new MailFileContent
            {
                Subject = message.Headers?.Subject ?? "",
                From = from == null ? "" : $"{from.DisplayName} <{from.Address}>".Trim(),
                Date = message.Headers == null ? (DateTime?)null : message.Headers.DateSent.LocalDateTime,
                Body = text ?? "",
            };
        }

        private static MailFileContent ReadMsg(string path)
        {
            using (var message = new MsgReader.Outlook.Storage.Message(path))
            {
                var text = message.BodyText;
                if (string.IsNullOrWhiteSpace(text)) text = HtmlText.ToPlainText(message.BodyHtml);
                return new MailFileContent
                {
                    Subject = message.Subject ?? "",
                    From = message.Sender == null ? "" : $"{message.Sender.DisplayName} <{message.Sender.Email}>".Trim(),
                    Date = message.SentOn?.LocalDateTime,
                    Body = text ?? "",
                };
            }
        }
    }

    /// <summary>
    /// 과거 답변 메일 1통을 섹션 1개("질문 + 답변")로 만든다. 최신 작성분(답변)의 서명과
    /// 직전 메일(고객 질문)보다 오래된 인용 이력은 제거한다.
    /// </summary>
    public sealed class EmailLoader : IDocumentLoader
    {
        private static readonly Regex HeaderLine = new Regex(
            @"^(From|Sent|Date|To|Cc|Subject|보낸 사람|보낸사람|보낸 날짜|날짜|받는 사람|받는사람|참조|제목)\s*:",
            RegexOptions.IgnoreCase);

        private static readonly Regex SeparatorLine = new Regex(@"^(-{2,}.*-{2,}|_{10,})$");

        public IReadOnlyCollection<string> Extensions { get; } = new[] { ".eml", ".msg" };

        public LoadedDocument Load(string path)
        {
            var mail = MailFileReader.Read(path);
            var title = string.IsNullOrWhiteSpace(mail.Subject) ? Path.GetFileNameWithoutExtension(path) : mail.Subject.Trim();
            var parts = EmailTextCleaner.Split(mail.Body);
            var answer = RemoveSignature(parts.Latest);
            var question = EmailTextCleaner.Split(StripLeadingHeaders(parts.Quoted)).Latest;

            var text = question.Length > 0 ? $"질문:\n{question}\n\n답변:\n{answer}" : answer;
            var doc = new LoadedDocument { Path = path, Kind = DocKind.Email, Title = title };
            if (text.Trim().Length > 0) doc.Sections.Add(new LoadedSection { Title = title, Text = text.Trim() });
            return doc;
        }

        private static string RemoveSignature(string text)
        {
            var lines = text.Split('\n').ToList();
            int sig = lines.FindIndex(l => l.TrimEnd() == "--");
            return (sig >= 0 ? string.Join("\n", lines.Take(sig)) : text).Trim();
        }

        private static string StripLeadingHeaders(string quoted)
        {
            var lines = quoted.Split('\n');
            int i = 0;
            while (i < lines.Length && IsPreamble(lines[i].Trim())) i++;
            return string.Join("\n", lines.Skip(i));
        }

        private static bool IsPreamble(string line) =>
            line.Length == 0 || HeaderLine.IsMatch(line) || SeparatorLine.IsMatch(line);
    }
}
