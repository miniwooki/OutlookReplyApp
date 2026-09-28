using System.Collections.Generic;

namespace TechSupportReply.Rag.Loaders
{
    public enum DocKind
    {
        Pdf,
        Word,
        Markdown,
        Text,
        Table,
        Email,
    }

    public sealed class LoadedSection
    {
        public string Title { get; set; } = "";
        /// <summary>PDF 페이지 번호(1부터). 페이지 개념이 없으면 null.</summary>
        public int? Page { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class LoadedDocument
    {
        public string Path { get; set; } = "";
        public DocKind Kind { get; set; }
        public string Title { get; set; } = "";
        public List<LoadedSection> Sections { get; set; } = new List<LoadedSection>();
    }
}
