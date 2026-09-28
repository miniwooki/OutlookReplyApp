using System.Collections.Generic;
using System.IO;
using System.Threading;
using TechSupportReply.Core.Knowledge;

namespace TechSupportReply.Indexer.Commands
{
    /// <summary>검색 결과를 확인하는 개발·관리자용 명령.</summary>
    internal static class SearchCommand
    {
        public static int Run(CliArgs args, TextWriter output, IndexerEnvironment env, CancellationToken ct)
        {
            var productId = args.Require("product");
            var query = args.Require("query");
            int top = args.GetInt("top", 8);
            using (var session = new RetrievalSession(args, env, ct))
            {
                if (session.Catalog.Find(productId) == null) throw new CliException($"알 수 없는 제품 id: {productId}");
                var result = session.Retriever.RetrieveAsync(productId, query, top, 3, ct).GetAwaiter().GetResult();
                Print(output, "근거 자료", result.References);
                Print(output, "문체 예시(과거 답변)", result.StyleExamples);
                PrintWarnings(output, result.Warnings);
            }
            return 0;
        }

        internal static void Print(TextWriter output, string heading, IReadOnlyList<KnowledgeChunk> chunks)
        {
            output.WriteLine($"== {heading} ({chunks.Count}) ==");
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                output.WriteLine($"{i + 1}. [{c.ProductId}] {c.Citation} ({c.SourceFile}) 점수 {c.Score:0.0000}");
                var snippet = c.Text.Replace("\n", " ");
                output.WriteLine("   " + (snippet.Length > 200 ? snippet.Substring(0, 200) + "…" : snippet));
            }
        }

        internal static void PrintWarnings(TextWriter output, IReadOnlyList<string> warnings)
        {
            if (warnings.Count == 0) return;
            output.WriteLine("== 경고 ==");
            foreach (var w in warnings) output.WriteLine("- " + w);
        }
    }
}
