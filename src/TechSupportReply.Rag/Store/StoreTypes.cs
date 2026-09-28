using System;

namespace TechSupportReply.Rag.Store
{
    /// <summary>Reference = 근거 자료(매뉴얼·FAQ·이슈), Reply = 과거 답변(문체 예시).</summary>
    public enum DocType
    {
        Reference = 0,
        Reply = 1,
    }

    public sealed class IndexedFile
    {
        public long Id { get; set; }
        /// <summary>제품 폴더 기준 상대 경로.</summary>
        public string RelativePath { get; set; } = "";
        public long Size { get; set; }
        public DateTime MtimeUtc { get; set; }
        public string Hash { get; set; } = "";
    }

    public sealed class NewChunk
    {
        public DocType DocType { get; set; }
        public string Title { get; set; } = "";
        public int? Page { get; set; }
        public string Text { get; set; } = "";
        public float[] Vector { get; set; }
    }

    public sealed class StoredChunk
    {
        public long Id { get; set; }
        public string RelativePath { get; set; } = "";
        public DocType DocType { get; set; }
        public string Title { get; set; } = "";
        public int? Page { get; set; }
        public string Text { get; set; } = "";
        public double Score { get; set; }
    }
}
