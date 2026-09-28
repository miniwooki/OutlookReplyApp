using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace TechSupportReply.Rag.Loaders
{
    /// <summary>DOCX를 제목 스타일 단위 섹션으로 읽는다. 표의 행은 셀을 " | "로 연결한다.</summary>
    public sealed class DocxLoader : IDocumentLoader
    {
        public IReadOnlyCollection<string> Extensions { get; } = new[] { ".docx" };

        public LoadedDocument Load(string path)
        {
            var fileTitle = Path.GetFileNameWithoutExtension(path);
            var doc = new LoadedDocument { Path = path, Kind = DocKind.Word, Title = fileTitle };
            using (var word = WordprocessingDocument.Open(path, false))
            {
                var body = word.MainDocumentPart?.Document?.Body;
                if (body == null) return doc;
                var title = fileTitle;
                var text = new StringBuilder();
                foreach (var element in body.ChildElements)
                {
                    if (element is Paragraph p)
                    {
                        var line = p.InnerText.Trim();
                        if (IsHeading(p) && line.Length > 0)
                        {
                            Flush(doc, title, text);
                            title = line;
                        }
                        else if (line.Length > 0)
                        {
                            text.Append(line).Append('\n');
                        }
                    }
                    else if (element is Table table)
                    {
                        foreach (var row in table.Elements<TableRow>())
                        {
                            var cells = row.Elements<TableCell>().Select(c => c.InnerText.Trim()).ToList();
                            if (cells.Any(c => c.Length > 0)) text.Append(string.Join(" | ", cells)).Append('\n');
                        }
                    }
                }
                Flush(doc, title, text);
            }
            return doc;
        }

        private static bool IsHeading(Paragraph p)
        {
            var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
            return style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                || style.StartsWith("제목", StringComparison.Ordinal)
                || style.Equals("Title", StringComparison.OrdinalIgnoreCase);
        }

        private static void Flush(LoadedDocument doc, string title, StringBuilder text)
        {
            var value = text.ToString().Trim();
            text.Clear();
            if (value.Length > 0) doc.Sections.Add(new LoadedSection { Title = title, Text = value });
        }
    }
}
