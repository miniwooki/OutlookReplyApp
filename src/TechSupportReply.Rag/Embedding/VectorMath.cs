using System;

namespace TechSupportReply.Rag.Embedding
{
    public static class VectorMath
    {
        public static float[] Normalize(float[] v)
        {
            double sum = 0;
            foreach (var x in v) sum += (double)x * x;
            var norm = Math.Sqrt(sum);
            var result = new float[v.Length];
            if (norm == 0) return result;
            for (int i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
            return result;
        }

        public static float Dot(float[] a, float[] b)
        {
            if (a.Length != b.Length) throw new ArgumentException("벡터 차원이 다릅니다.");
            float sum = 0;
            for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }

        public static byte[] ToBytes(float[] v)
        {
            var bytes = new byte[v.Length * sizeof(float)];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static float[] FromBytes(byte[] bytes)
        {
            var v = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, v, 0, v.Length * sizeof(float));
            return v;
        }
    }
}
