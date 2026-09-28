namespace TechSupportReply.Core.Products
{
    public enum ClassificationSource
    {
        Keyword,
        Llm,
        Default,
    }

    public sealed class ClassificationResult
    {
        public string ProductId { get; set; } = "";
        /// <summary>0~1.</summary>
        public double Confidence { get; set; }
        public string Reason { get; set; } = "";
        public ClassificationSource Source { get; set; }
    }
}
