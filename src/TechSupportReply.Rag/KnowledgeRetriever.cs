using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Knowledge;
using TechSupportReply.Core.Products;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Search;
using TechSupportReply.Rag.Store;
using TechSupportReply.Rag.Sync;

namespace TechSupportReply.Rag
{
    /// <summary>
    /// 애드인이 쓰는 지식 검색기. 공유 폴더를 주기적으로 로컬 캐시와 동기화하고, 캐시 색인에서
    /// 선택 제품 + 공통(_common)의 근거와 선택 제품의 과거 답변(문체 예시)을 찾는다.
    /// </summary>
    public sealed class KnowledgeRetriever : IKnowledgeRetriever, IDisposable
    {
        private sealed class OpenIndex
        {
            public string Path;
            public SqliteIndexStore Store;
            public HybridRetriever Retriever;
            public IEmbedder Embedder;
        }

        private readonly IndexCacheSync _sync;
        private readonly ProductCatalog _catalog;
        private readonly Func<IndexManifest, IEmbedder> _embedderFactory;
        private readonly TimeSpan _syncInterval;
        private readonly Dictionary<string, DateTime> _lastSync = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, OpenIndex> _open = new Dictionary<string, OpenIndex>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IEmbedder> _embedders = new Dictionary<string, IEmbedder>();
        private readonly object _lock = new object();
        private readonly object _syncLock = new object();

        public KnowledgeRetriever(IndexCacheSync sync, ProductCatalog catalog, Func<IndexManifest, IEmbedder> embedderFactory, TimeSpan? syncInterval = null)
        {
            _sync = sync ?? throw new ArgumentNullException(nameof(sync));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _embedderFactory = embedderFactory ?? (m => null);
            _syncInterval = syncInterval ?? TimeSpan.FromMinutes(10);
        }

        /// <summary>테스트용: 공유 폴더 동기화를 시작하기 직전에 호출된다.</summary>
        internal Action SyncStarting { get; set; }

        public Task<RetrievalResult> RetrieveAsync(string productId, string query, int referenceTopK, int styleTopK, CancellationToken ct) =>
            Task.Run(() => Retrieve(productId, query, referenceTopK, styleTopK, ct), ct);

        private RetrievalResult Retrieve(string productId, string query, int referenceTopK, int styleTopK, CancellationToken ct)
        {
            var result = new RetrievalResult();
            var warnings = new List<string>();
            var ids = new[] { productId, ProductCatalog.CommonId }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SyncIfDue(ids, warnings, ct);
            lock (_lock)
            {
                var embedder = Embedder(_sync.LoadLocalManifest());

                var rankings = new List<List<KeyValuePair<string, StoredChunk>>>();
                foreach (var id in ids)
                {
                    ct.ThrowIfCancellationRequested();
                    var index = Open(id, embedder);
                    if (index == null)
                    {
                        if (string.Equals(id, productId, StringComparison.OrdinalIgnoreCase))
                            warnings.Add($"'{_catalog.Find(id)?.DisplayName ?? id}' 지식 색인이 없어 RAG 없이 생성합니다.");
                        continue;
                    }
                    if (index.Retriever.DisabledReason != null) warnings.Add(index.Retriever.DisabledReason);
                    rankings.Add(index.Retriever.Search(query, referenceTopK, DocType.Reference)
                        .Select(c => new KeyValuePair<string, StoredChunk>(id, c)).ToList());

                    if (string.Equals(id, productId, StringComparison.OrdinalIgnoreCase) && styleTopK > 0)
                        result.StyleExamples.AddRange(index.Retriever.Search(query, styleTopK, DocType.Reply).Select(c => ToKnowledge(id, c)));
                }
                result.References.AddRange(Merge(rankings).Take(referenceTopK));
            }
            result.Warnings.AddRange(warnings.Distinct());
            return result;
        }

        /// <summary>
        /// 동기화(SMB 접근)는 검색 락 밖에서 한 번에 하나만 한다. 다른 스레드가 동기화 중이면 기다리지 않고
        /// 현재 캐시로 바로 검색한다(VPN 끊김으로 공유 폴더 접근이 오래 걸려도 검색이 멈추지 않도록).
        /// </summary>
        private void SyncIfDue(List<string> ids, List<string> warnings, CancellationToken ct)
        {
            if (!Monitor.TryEnter(_syncLock)) return;
            try
            {
                var now = DateTime.UtcNow;
                var due = ids.Where(id => !_lastSync.TryGetValue(id, out var t) || now - t >= _syncInterval).ToList();
                if (due.Count == 0) return;
                SyncStarting?.Invoke();
                var sync = _sync.Sync(due, ct);
                warnings.AddRange(sync.Warnings);
                foreach (var id in due) _lastSync[id] = now;
            }
            finally
            {
                Monitor.Exit(_syncLock);
            }
        }

        /// <summary>임베더를 모델별로 한 번만 만든다. 만들지 못했으면(모델 복사 실패 등) 다음 검색 때 다시 시도한다.</summary>
        private IEmbedder Embedder(IndexManifest manifest)
        {
            var key = manifest?.EmbeddingModel ?? "";
            if (_embedders.TryGetValue(key, out var cached)) return cached;
            var embedder = _embedderFactory(manifest);
            if (embedder != null) _embedders[key] = embedder;
            return embedder;
        }

        private OpenIndex Open(string productId, IEmbedder embedder)
        {
            var path = _sync.LocalIndexPath(productId);
            _open.TryGetValue(productId, out var current);
            if (current != null && current.Path == path && ReferenceEquals(current.Embedder, embedder)) return current;
            if (current != null)
            {
                current.Store.Dispose();
                _open.Remove(productId);
                _sync.CleanupUnreferenced();
            }
            if (path == null) return null;
            var store = SqliteIndexStore.OpenReadOnly(path);
            var opened = new OpenIndex { Path = path, Store = store, Retriever = new HybridRetriever(store, embedder), Embedder = embedder };
            _open[productId] = opened;
            return opened;
        }

        /// <summary>
        /// 제품·공통 색인의 결과를 각 색인 안에서 계산된 하이브리드 점수(같은 RRF 공식)로 합친다.
        /// 색인별 순위만으로 다시 융합하면 무관한 공통 자료의 1위도 제품 자료 1위와 같은 점수를 받는다.
        /// </summary>
        private static IEnumerable<KnowledgeChunk> Merge(List<List<KeyValuePair<string, StoredChunk>>> rankings) =>
            rankings
                .SelectMany((list, source) => list.Select((item, rank) => new { item, source, rank }))
                .OrderByDescending(x => x.item.Value.Score)
                .ThenBy(x => x.rank)
                .ThenBy(x => x.source)
                .Select(x => ToKnowledge(x.item.Key, x.item.Value));

        private static KnowledgeChunk ToKnowledge(string productId, StoredChunk c) => new KnowledgeChunk
        {
            ProductId = productId,
            SourceFile = c.RelativePath,
            Title = c.Title,
            Page = c.Page,
            Text = c.Text,
            IsReplyExample = c.DocType == DocType.Reply,
            Score = c.Score,
        };

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var index in _open.Values) index.Store.Dispose();
                _open.Clear();
                foreach (var embedder in _embedders.Values.OfType<IDisposable>()) embedder.Dispose();
                _embedders.Clear();
            }
        }
    }
}
