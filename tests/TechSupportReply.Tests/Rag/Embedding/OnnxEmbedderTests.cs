using System.Diagnostics;
using System.Linq;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace TechSupportReply.Tests.Rag.Embedding
{
    [Trait("Category", "Model")]
    public class OnnxEmbedderTests
    {
        private readonly ITestOutputHelper _output;

        public OnnxEmbedderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [SkippableFact]
        public void Embed_MatchesPythonReference()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            using (var embedder = new OnnxEmbedder(TestPaths.ModelDir))
            {
                foreach (var r in BgeReference.Load().Records)
                {
                    var cos = VectorMath.Dot(VectorMath.Normalize(r.Embedding), embedder.Embed(r.Text));
                    Assert.True(cos >= 0.999f, $"코사인 {cos:0.00000} < 0.999: \"{r.Text}\"");
                }
            }
        }

        [SkippableFact]
        public void ModelIdAndDimension()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            using (var embedder = new OnnxEmbedder(TestPaths.ModelDir))
            {
                Assert.Equal("bge-m3-int8", embedder.ModelId);
                Assert.Equal(1024, embedder.Dimension);
            }
        }

        [SkippableFact]
        public void KoreanQuery_IsCloserToRelatedEnglish_ThanUnrelated()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            using (var embedder = new OnnxEmbedder(TestPaths.ModelDir))
            {
                var q = embedder.Embed("접촉 초기 관통 경고");
                var related = VectorMath.Dot(q, embedder.Embed("initial penetration warning in contact definition"));
                var unrelated = VectorMath.Dot(q, embedder.Embed("license server installation guide"));
                Assert.True(related > unrelated, $"related={related:0.000}, unrelated={unrelated:0.000}");
            }
        }

        [SkippableFact]
        public void Performance_LogsChunkEmbeddingTime()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            var chunk = string.Concat(Enumerable.Repeat("LS-DYNA 접촉 정의에서 SOFT=2 옵션은 세그먼트 기반 접촉을 사용합니다. ", 30));
            using (var embedder = new OnnxEmbedder(TestPaths.ModelDir))
            {
                embedder.Embed(chunk);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 10; i++) embedder.Embed(chunk);
                _output.WriteLine($"청크 {chunk.Length}자 임베딩 평균 {sw.ElapsedMilliseconds / 10.0:0} ms");
            }
        }
    }
}
