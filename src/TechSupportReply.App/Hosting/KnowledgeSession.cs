using System;
using System.Collections.Generic;
using System.Linq;
using TechSupportReply.Core.Generation;
using TechSupportReply.Core.Knowledge;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Sync;

namespace TechSupportReply.App.Hosting
{
    /// <summary>현재 설정(RAG 루트)으로 만든 제품 목록·검색기·제품 지침 묶음. 설정이 바뀌면 새로 만든다.</summary>
    public sealed class KnowledgeSession : IDisposable
    {
        private readonly Func<ProductDefinition, string> _guideLoader;
        private readonly IDisposable _owned;

        public KnowledgeSession(ProductCatalog catalog, IKnowledgeRetriever retriever, Func<ProductDefinition, string> guideLoader,
            IEnumerable<string> warnings, IDisposable owned = null)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            Retriever = retriever;
            _guideLoader = guideLoader ?? (p => "");
            Warnings = (warnings ?? Enumerable.Empty<string>()).ToList();
            _owned = owned;
        }

        public ProductCatalog Catalog { get; }
        public IKnowledgeRetriever Retriever { get; }
        public IndexCacheSync Sync { get; internal set; }
        public IReadOnlyList<string> Warnings { get; }

        public string LoadGuide(ProductDefinition product) => _guideLoader(product);

        public ReplyGenerator CreateGenerator(AppSettings settings) => new ReplyGenerator(Catalog, Retriever, _guideLoader, settings);

        public void Dispose() => _owned?.Dispose();
    }
}
