using System;
using System.IO;
using System.Linq;
using TechSupportReply.Core.Products;
using TechSupportReply.Rag.Store;

namespace TechSupportReply.Rag.Indexing
{
    /// <summary>공유 지식 폴더(RAG 루트)의 경로 규칙.</summary>
    public static class KbLayout
    {
        public const string IndexFolder = "_index";
        public const string ModelsFolder = "_models";
        public const string ManifestFileName = "manifest.json";
        public const string ProductsFileName = "products.json";
        public const string RepliesFolder = "replies";

        public static string ProductDir(string root, ProductDefinition product) => Path.Combine(root, product.Folder);

        public static string IndexDir(string root) => Path.Combine(root, IndexFolder);

        public static string IndexFile(string root, string productId) => Path.Combine(IndexDir(root), IndexFileName(productId));

        public static string IndexFileName(string productId) => productId + ".sqlite";

        public static string ManifestPath(string root) => Path.Combine(IndexDir(root), ManifestFileName);

        public static string ProductsPath(string root) => Path.Combine(root, ProductsFileName);

        public static string PromptPath(string root, ProductDefinition product) =>
            Path.Combine(ProductDir(root, product), ProductCatalog.PromptFileName);

        public static string ModelDir(string root, string modelId) => Path.Combine(root, ModelsFolder, modelId);

        /// <summary>제품 폴더 기준 경로의 첫 조각이 replies면 과거 답변(문체 예시)이다.</summary>
        public static DocType DocTypeFor(string relativePath)
        {
            var first = Segments(relativePath).FirstOrDefault() ?? "";
            return string.Equals(first, RepliesFolder, StringComparison.OrdinalIgnoreCase) && Segments(relativePath).Length > 1
                ? DocType.Reply
                : DocType.Reference;
        }

        /// <summary>제품 지침 파일, Office 임시 파일(~$), 숨김 파일, '_'로 시작하는 폴더 안의 파일은 색인하지 않는다.</summary>
        public static bool IsIgnored(string relativePath)
        {
            var segments = Segments(relativePath);
            if (segments.Length == 0) return true;
            var name = segments[segments.Length - 1];
            if (string.Equals(name, ProductCatalog.PromptFileName, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("~$") || name.StartsWith(".")) return true;
            return segments.Take(segments.Length - 1).Any(s => s.StartsWith("_") || s.StartsWith("."));
        }

        private static string[] Segments(string relativePath) =>
            (relativePath ?? "").Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
