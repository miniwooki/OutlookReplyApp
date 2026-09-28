using System;
using System.IO;
using System.Linq;
using TechSupportReply.Core.IO;

namespace TechSupportReply.Rag.Indexing
{
    /// <summary>
    /// 관리자 PC에서 만든 색인을 공유 폴더에 게시한다. SMB 위의 SQLite를 직접 쓰지 않도록
    /// 로컬 작업 사본에서 빌드한 뒤 임시 파일 → 원자적 교체로 올리고 매니페스트를 갱신한다.
    /// </summary>
    public sealed class IndexPublisher
    {
        private readonly string _root;

        public IndexPublisher(string ragRoot)
        {
            _root = ragRoot ?? throw new ArgumentNullException(nameof(ragRoot));
        }

        /// <summary>공유 색인이 있으면 작업 폴더로 복사한다. 반환값은 작업 색인 경로(파일이 없을 수 있음).</summary>
        public string PrepareWorkingCopy(string productId, string workDir)
        {
            Directory.CreateDirectory(workDir);
            var work = Path.Combine(workDir, KbLayout.IndexFileName(productId));
            var shared = KbLayout.IndexFile(_root, productId);
            if (File.Exists(shared)) File.Copy(shared, work, true);
            else if (File.Exists(work)) File.Delete(work);
            return work;
        }

        public ProductIndexInfo Publish(string productId, string workingIndexPath, string embeddingModel, int dimension, IndexBuildReport report)
        {
            var target = KbLayout.IndexFile(_root, productId);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var tmp = target + ".tmp";
            File.Copy(workingIndexPath, tmp, true);
            var hash = FileHash.Sha256(tmp);
            AtomicFile.Replace(tmp, target);

            var manifestPath = KbLayout.ManifestPath(_root);
            var manifest = IndexManifest.Load(manifestPath) ?? new IndexManifest();
            manifest.EmbeddingModel = embeddingModel;
            manifest.Dimension = dimension;
            var previous = manifest.Find(productId);
            var info = new ProductIndexInfo
            {
                ProductId = productId,
                File = KbLayout.IndexFileName(productId),
                Version = (previous?.Version ?? 0) + 1,
                Sha256 = hash,
                BuiltAtUtc = DateTime.UtcNow,
                FileCount = report?.FileCount ?? 0,
                ChunkCount = report?.ChunkCount ?? 0,
            };
            manifest.Products = manifest.Products
                .Where(p => !string.Equals(p.ProductId, productId, StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { info })
                .OrderBy(p => p.ProductId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            manifest.Save(manifestPath);
            return info;
        }
    }
}
