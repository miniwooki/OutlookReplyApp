using System.Collections.Generic;
using System.IO;

namespace TechSupportReply.Rag.Loaders
{
    public sealed class TextLoader : IDocumentLoader
    {
        public IReadOnlyCollection<string> Extensions { get; } = new[] { ".txt", ".log" };

        public LoadedDocument Load(string path)
        {
            var title = Path.GetFileNameWithoutExtension(path);
            var text = TextFileReader.ReadAllText(path).Replace("\r\n", "\n").Trim();
            var doc = new LoadedDocument { Path = path, Kind = DocKind.Text, Title = title };
            if (text.Length > 0) doc.Sections.Add(new LoadedSection { Title = title, Text = text });
            return doc;
        }
    }
}
