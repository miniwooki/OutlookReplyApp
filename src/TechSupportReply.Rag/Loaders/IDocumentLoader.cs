using System.Collections.Generic;

namespace TechSupportReply.Rag.Loaders
{
    public interface IDocumentLoader
    {
        /// <summary>처리하는 확장자(소문자, 점 포함).</summary>
        IReadOnlyCollection<string> Extensions { get; }

        LoadedDocument Load(string path);
    }
}
