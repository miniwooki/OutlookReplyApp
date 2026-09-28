using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TechSupportReply.Rag.Loaders
{
    /// <summary>제목(#~######) 단위로 섹션을 나눈다. 코드 블록 안의 #은 제목으로 보지 않는다.</summary>
    public sealed class MarkdownLoader : IDocumentLoader
    {
        private static readonly Regex Heading = new Regex(@"^#{1,6}\s+(.+?)\s*#*\s*$");

        public IReadOnlyCollection<string> Extensions { get; } = new[] { ".md", ".markdown" };

        public LoadedDocument Load(string path)
        {
            var fileTitle = Path.GetFileNameWithoutExtension(path);
            var doc = new LoadedDocument { Path = path, Kind = DocKind.Markdown, Title = fileTitle };
            var title = fileTitle;
            var body = new StringBuilder();
            bool inFence = false;
            foreach (var line in TextFileReader.ReadAllText(path).Replace("\r\n", "\n").Split('\n'))
            {
                if (line.TrimStart().StartsWith("```")) inFence = !inFence;
                var m = inFence ? Match.Empty : Heading.Match(line);
                if (m.Success)
                {
                    Flush(doc, title, body);
                    title = m.Groups[1].Value;
                }
                else
                {
                    body.Append(line).Append('\n');
                }
            }
            Flush(doc, title, body);
            return doc;
        }

        private static void Flush(LoadedDocument doc, string title, StringBuilder body)
        {
            var text = body.ToString().Trim();
            body.Clear();
            if (text.Length > 0) doc.Sections.Add(new LoadedSection { Title = title, Text = text });
        }
    }
}
