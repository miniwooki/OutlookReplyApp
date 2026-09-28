using System;
using System.IO;

namespace TechSupportReply.Rag.Loaders
{
    /// <summary>파일을 읽을 수 없을 때 사유(한국어)와 함께 던진다. 색인 빌더는 이 파일만 건너뛴다.</summary>
    public sealed class DocumentLoadException : Exception
    {
        public DocumentLoadException(string path, string reason, Exception inner = null)
            : base($"{Path.GetFileName(path)}: {reason}", inner)
        {
            FilePath = path;
            Reason = reason;
        }

        public string FilePath { get; }
        public string Reason { get; }
    }
}
