using System;
using System.IO;
using System.Text;
using System.Threading;
using TechSupportReply.Core.Products;
using TechSupportReply.Rag;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Sync;

namespace TechSupportReply.Indexer.Commands
{
    /// <summary>search/reply 명령이 애드인과 같은 방식(캐시 동기화 → 검색)으로 지식을 찾도록 구성한다.</summary>
    internal sealed class RetrievalSession : IDisposable
    {
        public RetrievalSession(CliArgs args, IndexerEnvironment env, CancellationToken ct)
        {
            var root = args.Require("root");
            Sync = new IndexCacheSync(root, args.Get("cache") ?? IndexCacheSync.DefaultCacheDir);
            Catalog = LoadCatalog(root, Sync);
            var modelOverride = args.Get("model");
            Retriever = new KnowledgeRetriever(Sync, Catalog, manifest =>
            {
                var dir = modelOverride ?? Sync.EnsureModel(manifest?.EmbeddingModel, ct);
                return dir == null ? null : env.CreateEmbedder(dir);
            });
        }

        public IndexCacheSync Sync { get; }
        public ProductCatalog Catalog { get; }
        public KnowledgeRetriever Retriever { get; }

        public string LoadGuide(ProductDefinition product)
        {
            var path = Sync.LocalPromptPath(product.Id);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        }

        public void Dispose() => Retriever.Dispose();

        private static ProductCatalog LoadCatalog(string root, IndexCacheSync sync)
        {
            try
            {
                if (File.Exists(KbLayout.ProductsPath(root))) return ProductCatalog.Load(KbLayout.ProductsPath(root));
            }
            catch (IOException)
            {
            }
            return ProductCatalog.Load(sync.LocalProductsPath);
        }
    }
}
