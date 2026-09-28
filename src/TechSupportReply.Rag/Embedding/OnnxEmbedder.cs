using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TechSupportReply.Rag.Embedding
{
    /// <summary>bge-m3 ONNX 임베더: CLS 토큰 은닉 상태를 L2 정규화해 반환한다.</summary>
    public sealed class OnnxEmbedder : IEmbedder, IDisposable
    {
        private readonly InferenceSession _session;
        private readonly BgeM3Tokenizer _tokenizer;
        private readonly int _maxTokens;
        private readonly object _lock = new object();

        public string ModelId { get; }
        public int Dimension { get; }

        public OnnxEmbedder(string modelDir, int maxTokens = 512)
        {
            ModelId = new DirectoryInfo(modelDir).Name;
            _tokenizer = new BgeM3Tokenizer(Path.Combine(modelDir, "sentencepiece.bpe.model"));
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _session = new InferenceSession(Path.Combine(modelDir, "model.onnx"), options);
            _maxTokens = maxTokens;
            Dimension = Embed("dimension probe").Length;
        }

        public float[] Embed(string text)
        {
            var ids = _tokenizer.Encode(text, _maxTokens);
            int n = ids.Count;
            var inputIds = new DenseTensor<long>(new[] { 1, n });
            var mask = new DenseTensor<long>(new[] { 1, n });
            for (int i = 0; i < n; i++)
            {
                inputIds[0, i] = ids[i];
                mask[0, i] = 1;
            }

            var inputs = new List<NamedOnnxValue>();
            foreach (var name in _session.InputMetadata.Keys)
            {
                if (name == "input_ids") inputs.Add(NamedOnnxValue.CreateFromTensor(name, inputIds));
                else if (name == "attention_mask") inputs.Add(NamedOnnxValue.CreateFromTensor(name, mask));
                else if (name == "token_type_ids") inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { 1, n })));
            }

            lock (_lock)
            {
                using (var results = _session.Run(inputs))
                {
                    var output = results.FirstOrDefault(r => r.Name == "last_hidden_state") ?? results.First();
                    var tensor = output.AsTensor<float>();
                    int dim = tensor.Dimensions[tensor.Dimensions.Length - 1];
                    var vector = new float[dim];
                    for (int d = 0; d < dim; d++)
                        vector[d] = tensor.Dimensions.Length == 3 ? tensor[0, 0, d] : tensor[0, d];
                    return VectorMath.Normalize(vector);
                }
            }
        }

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}
