using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Rag.Store;

namespace TechSupportReply.Rag.Search
{
    /// <summary>
    /// 한 색인에 대해 FTS5 BM25 후보와 코사인 유사도 후보를 RRF로 융합한다.
    /// 색인의 임베딩 모델이 로컬 모델과 다르거나 모델이 없으면 키워드 검색만 한다.
    /// </summary>
    public sealed class HybridRetriever
    {
        public const string MetaEmbeddingModel = "embedding_model";
        public const string MetaDimension = "dimension";

        private readonly SqliteIndexStore _store;
        private readonly IEmbedder _embedder;
        private readonly Dictionary<string, IReadOnlyList<KeyValuePair<long, float[]>>> _vectors =
            new Dictionary<string, IReadOnlyList<KeyValuePair<long, float[]>>>();
        private readonly object _lock = new object();

        public HybridRetriever(SqliteIndexStore store, IEmbedder embedder)
        {
            _store = store;
            _embedder = embedder;
            var model = store.GetMeta(MetaEmbeddingModel);
            var dimension = store.GetMeta(MetaDimension);
            if (embedder == null)
            {
                DisabledReason = "임베딩 모델이 없어 키워드 검색만 사용합니다.";
            }
            else if (model != embedder.ModelId || dimension != embedder.Dimension.ToString(CultureInfo.InvariantCulture))
            {
                DisabledReason = $"색인의 임베딩 모델({model ?? "없음"})과 로컬 모델({embedder.ModelId})이 달라 키워드 검색만 사용합니다.";
            }
            else
            {
                VectorEnabled = true;
            }
        }

        public bool VectorEnabled { get; }

        /// <summary>벡터 검색이 꺼진 사유(켜져 있으면 null).</summary>
        public string DisabledReason { get; }

        public IReadOnlyList<StoredChunk> Search(string query, int topK, DocType? type = null, int candidatePool = 30)
        {
            if (topK <= 0 || string.IsNullOrWhiteSpace(query)) return new List<StoredChunk>();
            var rankings = new List<IReadOnlyList<long>>
            {
                _store.SearchKeyword(query, candidatePool, type).Select(c => c.Id).ToList(),
            };
            if (VectorEnabled) rankings.Add(VectorRanking(query, candidatePool, type));

            var fused = Rrf.Fuse(rankings).Take(topK).ToList();
            var chunks = _store.GetChunks(fused.Select(f => f.Key));
            var scores = fused.ToDictionary(f => f.Key, f => f.Value);
            foreach (var c in chunks) c.Score = scores[c.Id];
            return chunks;
        }

        private List<long> VectorRanking(string query, int pool, DocType? type)
        {
            var q = _embedder.Embed(query);
            return Vectors(type)
                .Select(v => new KeyValuePair<long, float>(v.Key, VectorMath.Dot(q, v.Value)))
                .OrderByDescending(p => p.Value)
                .Take(pool)
                .Select(p => p.Key)
                .ToList();
        }

        private IReadOnlyList<KeyValuePair<long, float[]>> Vectors(DocType? type)
        {
            var key = type?.ToString() ?? "*";
            lock (_lock)
            {
                if (!_vectors.TryGetValue(key, out var list))
                {
                    list = _store.LoadVectors(type);
                    _vectors[key] = list;
                }
                return list;
            }
        }
    }
}
