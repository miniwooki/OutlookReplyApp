using System;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Embedding;

namespace TechSupportReply.Indexer
{
    /// <summary>명령이 쓰는 외부 의존성(임베딩 모델, 환경 변수, LLM). 테스트에서 가짜로 바꾼다.</summary>
    internal sealed class IndexerEnvironment
    {
        public Func<string, IEmbedder> CreateEmbedder { get; set; } = dir => new OnnxEmbedder(dir);
        public Func<string, string> GetEnv { get; set; } = Environment.GetEnvironmentVariable;
        public Func<LlmProfile, string, ILlmProvider> CreateLlm { get; set; } = LlmProviderFactory.Create;
    }
}
