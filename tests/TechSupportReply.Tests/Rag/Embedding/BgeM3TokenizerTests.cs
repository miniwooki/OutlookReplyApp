using System.IO;
using System.Linq;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Embedding
{
    [Trait("Category", "Model")]
    public class BgeM3TokenizerTests
    {
        private static BgeM3Tokenizer Create() =>
            new BgeM3Tokenizer(Path.Combine(TestPaths.ModelDir, "sentencepiece.bpe.model"));

        [SkippableFact]
        public void Encode_MatchesPythonTokenizerIds()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            var tokenizer = Create();
            var mismatches = BgeReference.Load().Records
                .Select(r => new { r.Text, Expected = r.Ids, Actual = tokenizer.Encode(r.Text, 512).ToArray() })
                .Where(x => !x.Expected.SequenceEqual(x.Actual))
                .Select(x => $"\"{x.Text}\"\n  기대: {string.Join(",", x.Expected)}\n  실제: {string.Join(",", x.Actual)}")
                .ToList();
            Assert.True(mismatches.Count == 0, "토큰 불일치:\n" + string.Join("\n", mismatches));
        }

        [SkippableFact]
        public void Encode_TruncatesToMaxLength_KeepingClsAndEos()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            var ids = Create().Encode(string.Join(" ", Enumerable.Repeat("contact", 2000)), 16);
            Assert.Equal(16, ids.Count);
            Assert.Equal(BgeM3Tokenizer.ClsId, ids[0]);
            Assert.Equal(BgeM3Tokenizer.EosId, ids[15]);
        }

        [SkippableFact]
        public void Encode_EmptyText_ReturnsClsEos()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            Assert.Equal(new[] { 0, 2 }, Create().Encode("", 512).ToArray());
        }
    }
}
