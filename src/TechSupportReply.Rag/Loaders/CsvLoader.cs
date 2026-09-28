using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TechSupportReply.Rag.Loaders
{
    public sealed class CsvLoader : IDocumentLoader
    {
        public IReadOnlyCollection<string> Extensions { get; } = new[] { ".csv", ".tsv" };

        public LoadedDocument Load(string path)
        {
            var title = Path.GetFileNameWithoutExtension(path);
            var rows = CsvParser.Parse(TextFileReader.ReadAllText(path));
            return new LoadedDocument { Path = path, Kind = DocKind.Table, Title = title, Sections = TableSections.FromRows(rows, title) };
        }
    }

    /// <summary>RFC 4180 CSV 파서. 구분자는 첫 줄에서 `,` `\t` `;` 중 가장 많은 것으로 판별한다.</summary>
    public static class CsvParser
    {
        public static List<string[]> Parse(string text)
        {
            var rows = new List<string[]>();
            if (string.IsNullOrEmpty(text)) return rows;
            char delimiter = DetectDelimiter(text);
            var record = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            bool recordStarted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c != '"') field.Append(c);
                    else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                    continue;
                }
                if (c == '"')
                {
                    inQuotes = true;
                    recordStarted = true;
                }
                else if (c == delimiter)
                {
                    record.Add(field.ToString());
                    field.Clear();
                    recordStarted = true;
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    record.Add(field.ToString());
                    field.Clear();
                    rows.Add(record.ToArray());
                    record.Clear();
                    recordStarted = false;
                }
                else
                {
                    field.Append(c);
                    recordStarted = true;
                }
            }
            if (recordStarted)
            {
                record.Add(field.ToString());
                rows.Add(record.ToArray());
            }
            return rows;
        }

        private static char DetectDelimiter(string text)
        {
            int end = text.IndexOfAny(new[] { '\r', '\n' });
            var first = end < 0 ? text : text.Substring(0, end);
            var best = new[] { ',', '\t', ';' }.OrderByDescending(d => first.Count(ch => ch == d)).First();
            return first.IndexOf(best) >= 0 ? best : ',';
        }
    }

    /// <summary>표의 각 행을 "머리글: 값" 줄로 이은 섹션으로 만든다. 빈 값과 빈 행은 생략한다.</summary>
    public static class TableSections
    {
        public static List<LoadedSection> FromRows(IReadOnlyList<string[]> rows, string title)
        {
            var sections = new List<LoadedSection>();
            if (rows == null || rows.Count < 2) return sections;
            var header = rows[0].Select(h => (h ?? "").Trim()).ToArray();
            for (int r = 1; r < rows.Count; r++)
            {
                var lines = new List<string>();
                for (int c = 0; c < rows[r].Length; c++)
                {
                    var value = (rows[r][c] ?? "").Trim();
                    if (value.Length == 0) continue;
                    var name = c < header.Length && header[c].Length > 0 ? header[c] : $"열{c + 1}";
                    lines.Add($"{name}: {value}");
                }
                if (lines.Count == 0) continue;
                sections.Add(new LoadedSection { Title = $"{title} #{r}", Text = string.Join("\n", lines) });
            }
            return sections;
        }
    }
}
