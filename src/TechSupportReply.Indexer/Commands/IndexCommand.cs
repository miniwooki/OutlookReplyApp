using System;
using System.IO;
using System.Linq;
using System.Threading;
using TechSupportReply.Core.Products;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Loaders;

namespace TechSupportReply.Indexer.Commands
{
    /// <summary>제품 폴더를 색인하고 공유 폴더 _index에 게시한다(관리자용).</summary>
    internal static class IndexCommand
    {
        public const string DefaultModelId = "bge-m3-int8";

        public static int Run(CliArgs args, TextWriter output, IndexerEnvironment env, CancellationToken ct)
        {
            var root = args.Require("root");
            if (!Directory.Exists(root)) throw new InvalidOperationException($"RAG 루트 폴더가 없습니다: {root}");
            var modelDir = args.Get("model") ?? env.GetEnv("TSR_MODEL_DIR") ?? KbLayout.ModelDir(root, DefaultModelId);
            if (!Directory.Exists(modelDir))
                throw new InvalidOperationException($"임베딩 모델 폴더가 없습니다: {modelDir}\n tools/download_model.sh로 받은 bge-m3-int8 폴더를 {Path.Combine(root, KbLayout.ModelsFolder)}에 복사하세요.");

            var catalog = ProductCatalog.Load(KbLayout.ProductsPath(root));
            var productId = args.Get("product");
            var products = productId == null
                ? catalog.Products.ToList()
                : new[] { catalog.Find(productId) ?? throw new CliException($"알 수 없는 제품 id: {productId}") }.ToList();
            var workDir = args.Get("work") ?? Path.Combine(Path.GetTempPath(), "TechSupportReply-index");
            bool full = args.Has("full");

            var embedder = env.CreateEmbedder(modelDir);
            try
            {
                output.WriteLine($"임베딩 모델: {embedder.ModelId} ({embedder.Dimension}차원)");
                var builder = new IndexBuilder(DocumentLoaderRegistry.CreateDefault(), new Chunker(), embedder);
                var publisher = new IndexPublisher(root);
                int skippedTotal = 0;
                foreach (var product in products)
                {
                    ct.ThrowIfCancellationRequested();
                    output.WriteLine($"[{product.Id}] {product.DisplayName} 색인 중...");
                    var work = publisher.PrepareWorkingCopy(product.Id, workDir);
                    var report = builder.Build(product.Id, KbLayout.ProductDir(root, product), work, full, new ConsoleProgress(output), ct);
                    var info = publisher.Publish(product.Id, work, embedder.ModelId, embedder.Dimension, report);
                    output.WriteLine($"{product.Id}: 추가 {report.Added}, 갱신 {report.Updated}, 삭제 {report.Removed}, 유지 {report.Unchanged}, " +
                                     $"파일 {report.FileCount}, 청크 {report.ChunkCount}, 건너뜀 {report.Skipped.Count} " +
                                     $"({report.Elapsed.TotalSeconds:0.0}초) → 버전 {info.Version}");
                    foreach (var s in report.Skipped) output.WriteLine($"  건너뜀: {s.RelativePath} — {s.Reason}");
                    skippedTotal += report.Skipped.Count;
                }
                output.WriteLine(skippedTotal == 0 ? "완료했습니다." : $"완료했습니다(읽지 못한 파일 {skippedTotal}개).");
                return 0;
            }
            finally
            {
                (embedder as IDisposable)?.Dispose();
            }
        }
    }
}
