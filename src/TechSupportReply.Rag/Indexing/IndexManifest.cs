using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TechSupportReply.Core.IO;
using TechSupportReply.Core.Serialization;

namespace TechSupportReply.Rag.Indexing
{
    public sealed class ProductIndexInfo
    {
        public string ProductId { get; set; } = "";
        /// <summary>_index 폴더 기준 파일 이름.</summary>
        public string File { get; set; } = "";
        public int Version { get; set; }
        public string Sha256 { get; set; } = "";
        public DateTime BuiltAtUtc { get; set; }
        public int FileCount { get; set; }
        public int ChunkCount { get; set; }
    }

    /// <summary>공유 폴더 _index\manifest.json. 클라이언트는 버전·해시를 비교해 바뀐 색인만 복사한다.</summary>
    public sealed class IndexManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string EmbeddingModel { get; set; } = "";
        public int Dimension { get; set; }
        public List<ProductIndexInfo> Products { get; set; } = new List<ProductIndexInfo>();

        public ProductIndexInfo Find(string productId) =>
            Products.FirstOrDefault(p => string.Equals(p.ProductId, productId, StringComparison.OrdinalIgnoreCase));

        public static IndexManifest Load(string path)
        {
            if (!System.IO.File.Exists(path)) return null;
            try
            {
                var manifest = JsonSerializer.Deserialize<IndexManifest>(System.IO.File.ReadAllText(path, Encoding.UTF8), JsonDefaults.Options);
                if (manifest != null && manifest.Products == null) manifest.Products = new List<ProductIndexInfo>();
                return manifest;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"manifest.json 형식 오류: {ex.Message}", ex);
            }
        }

        public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
    }
}
