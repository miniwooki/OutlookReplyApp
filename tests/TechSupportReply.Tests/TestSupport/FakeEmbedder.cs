using System.Collections.Generic;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Rag.Search;

namespace TechSupportReply.Tests.TestSupport
{
    /// <summary>
    /// 결정적 가짜 임베더. 용어를 해시 버킷에 더하므로 같은 용어(또는 Synonyms로 묶은 용어)를
    /// 공유하는 텍스트끼리 코사인이 높다.
    /// </summary>
    internal sealed class FakeEmbedder : IEmbedder
    {
        public const string Id = "fake";

        public string ModelId => Id;
        public int Dimension => 64;
        public int Calls { get; private set; }

        public Dictionary<string, string> Synonyms { get; } = new Dictionary<string, string>
        {
            ["관통"] = "penetration",
            ["경고"] = "warning",
            ["초기"] = "initial",
            ["발산"] = "divergence",
        };

        public float[] Embed(string text)
        {
            Calls++;
            var v = new float[Dimension];
            foreach (var term in SearchTextNormalizer.Terms(text))
            {
                var canonical = Synonyms.TryGetValue(term, out var s) ? s : term;
                v[Bucket(canonical)] += 1;
            }
            return VectorMath.Normalize(v);
        }

        private int Bucket(string term)
        {
            uint h = 2166136261;
            foreach (var c in term) h = (h ^ c) * 16777619;
            return (int)(h % (uint)Dimension);
        }
    }
}
