using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TechSupportReply.Core.Knowledge
{
    public sealed class KnowledgeChunk
    {
        public string ProductId { get; set; } = "";
        /// <summary>제품 폴더 기준 상대 경로.</summary>
        public string SourceFile { get; set; } = "";
        public string Title { get; set; } = "";
        public int? Page { get; set; }
        public string Text { get; set; } = "";
        public bool IsReplyExample { get; set; }
        public double Score { get; set; }

        public string Citation
        {
            get
            {
                var name = string.IsNullOrEmpty(SourceFile) ? "(출처 미상)" : Path.GetFileName(SourceFile);
                return Page.HasValue ? $"{name} p.{Page.Value}" : name;
            }
        }
    }

    public sealed class RetrievalResult
    {
        public List<KnowledgeChunk> References { get; } = new List<KnowledgeChunk>();
        public List<KnowledgeChunk> StyleExamples { get; } = new List<KnowledgeChunk>();
        public List<string> Warnings { get; } = new List<string>();
    }

    public interface IKnowledgeRetriever
    {
        Task<RetrievalResult> RetrieveAsync(string productId, string query, int referenceTopK, int styleTopK, CancellationToken ct);
    }
}
