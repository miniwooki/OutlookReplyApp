using System;
using TechSupportReply.Rag.Embedding;
using Xunit;

namespace TechSupportReply.Tests.Rag.Embedding
{
    public class VectorMathTests
    {
        [Fact]
        public void Normalize_ProducesUnitLength()
        {
            var v = VectorMath.Normalize(new float[] { 3, 4 });
            Assert.Equal(0.6f, v[0], 5);
            Assert.Equal(0.8f, v[1], 5);
        }

        [Fact]
        public void Normalize_ZeroVector_ReturnsZeros()
        {
            Assert.Equal(new float[] { 0, 0 }, VectorMath.Normalize(new float[] { 0, 0 }));
        }

        [Fact]
        public void Dot_ComputesSum()
        {
            Assert.Equal(11f, VectorMath.Dot(new float[] { 1, 2 }, new float[] { 3, 4 }));
        }

        [Fact]
        public void Dot_DimensionMismatch_Throws()
        {
            Assert.Throws<ArgumentException>(() => VectorMath.Dot(new float[] { 1 }, new float[] { 1, 2 }));
        }

        [Fact]
        public void Bytes_RoundTrip()
        {
            var v = new[] { 0.25f, -1.5f, 3.125f };
            Assert.Equal(v, VectorMath.FromBytes(VectorMath.ToBytes(v)));
        }
    }
}
