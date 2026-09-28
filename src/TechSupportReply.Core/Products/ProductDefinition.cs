using System.Collections.Generic;

namespace TechSupportReply.Core.Products
{
    public sealed class ProductDefinition
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        /// <summary>RAG 루트 아래의 제품 폴더 이름. 비어 있으면 Id를 사용한다.</summary>
        public string Folder { get; set; } = "";
        public List<string> Keywords { get; set; } = new List<string>();
    }

    internal sealed class ProductCatalogFile
    {
        public List<ProductDefinition> Products { get; set; } = new List<ProductDefinition>();
    }
}
