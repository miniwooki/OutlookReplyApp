namespace TechSupportReply.Rag.Embedding
{
    public interface IEmbedder
    {
        /// <summary>색인 호환성 판정용 모델 식별자(모델 폴더 이름).</summary>
        string ModelId { get; }
        int Dimension { get; }
        /// <summary>L2 정규화된 벡터를 반환한다.</summary>
        float[] Embed(string text);
    }
}
