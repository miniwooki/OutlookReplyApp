using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TechSupportReply.Core.IO;
using TechSupportReply.Core.Products;
using TechSupportReply.Rag.Indexing;

namespace TechSupportReply.Rag.Sync
{
    public sealed class SyncResult
    {
        public bool SharedReachable { get; set; }
        public List<string> UpdatedProducts { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public IndexManifest LocalManifest { get; set; }
    }

    /// <summary>
    /// 공유 지식 폴더의 색인·제품 목록·제품 지침·임베딩 모델을 로컬 캐시로 복사한다.
    /// 캐시 색인 파일은 버전별 이름을 쓰므로 열려 있는 이전 버전과 충돌하지 않는다.
    /// 공유 폴더에 접근할 수 없으면 예외 없이 경고와 함께 기존 캐시를 그대로 쓴다.
    /// </summary>
    public sealed class IndexCacheSync
    {
        private static readonly string[] RequiredModelFiles = { "model.onnx", "sentencepiece.bpe.model" };

        private readonly string _root;
        private readonly string _cacheDir;
        private readonly object _lock = new object();

        public IndexCacheSync(string ragRoot, string cacheDir)
        {
            _root = ragRoot ?? "";
            _cacheDir = cacheDir ?? throw new ArgumentNullException(nameof(cacheDir));
        }

        public static string DefaultCacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TechSupportReply", "cache");

        public string RagRoot => _root;

        private string IndexDir => Path.Combine(_cacheDir, "index");

        private string LocalManifestPath => Path.Combine(IndexDir, KbLayout.ManifestFileName);

        public string LocalProductsPath => Path.Combine(_cacheDir, KbLayout.ProductsFileName);

        public string LocalPromptPath(string productId) => Path.Combine(_cacheDir, "prompts", productId + ".md");

        public IndexManifest LoadLocalManifest()
        {
            try
            {
                return IndexManifest.Load(LocalManifestPath);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>캐시에 있는 제품 색인 경로. 없으면 null.</summary>
        public string LocalIndexPath(string productId)
        {
            var info = LoadLocalManifest()?.Find(productId);
            if (info == null) return null;
            var path = Path.Combine(IndexDir, info.File);
            return File.Exists(path) ? path : null;
        }

        public SyncResult Sync(IEnumerable<string> productIds, CancellationToken ct)
        {
            lock (_lock)
            {
                var result = new SyncResult();
                var local = LoadLocalManifest() ?? new IndexManifest();
                try
                {
                    if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
                        throw new DirectoryNotFoundException(_root);
                    result.SharedReachable = true;
                    SyncShared(productIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), local, result, ct);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.SharedReachable = false;
                    var hasCache = local.Products.Any(p => File.Exists(Path.Combine(IndexDir, p.File)));
                    result.Warnings.Add($"공유 지식 폴더({_root})에 접근할 수 없습니다. "
                                        + (hasCache ? "캐시된 색인을 사용합니다." : "캐시된 색인이 없습니다."));
                }
                result.LocalManifest = LoadLocalManifest() ?? local;
                return result;
            }
        }

        /// <summary>임베딩 모델 폴더를 캐시로 복사하고 로컬 경로를 반환한다. 공유·캐시 어디에도 없으면 null.</summary>
        public string EnsureModel(string modelId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return null;
            var localDir = Path.Combine(_cacheDir, "models", modelId);
            if (IsCompleteModel(localDir)) return localDir;
            try
            {
                var sharedDir = KbLayout.ModelDir(_root, modelId);
                if (!IsCompleteModel(sharedDir)) return null;
                Directory.CreateDirectory(localDir);
                foreach (var file in Directory.GetFiles(sharedDir))
                {
                    ct.ThrowIfCancellationRequested();
                    CopyAtomic(file, Path.Combine(localDir, Path.GetFileName(file)));
                }
                return IsCompleteModel(localDir) ? localDir : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private void SyncShared(List<string> productIds, IndexManifest local, SyncResult result, CancellationToken ct)
        {
            var sharedProducts = KbLayout.ProductsPath(_root);
            if (File.Exists(sharedProducts)) CopyAtomic(sharedProducts, LocalProductsPath);
            ProductCatalog catalog;
            try
            {
                catalog = ProductCatalog.Load(sharedProducts);
            }
            catch (InvalidDataException ex)
            {
                result.Warnings.Add(ex.Message);
                catalog = ProductCatalog.CreateDefault();
            }

            foreach (var id in productIds)
            {
                var product = catalog.Find(id);
                if (product == null) continue;
                var prompt = KbLayout.PromptPath(_root, product);
                var localPrompt = LocalPromptPath(product.Id);
                if (File.Exists(prompt)) CopyAtomic(prompt, localPrompt);
                else if (File.Exists(localPrompt)) File.Delete(localPrompt);
            }

            IndexManifest shared;
            try
            {
                shared = IndexManifest.Load(KbLayout.ManifestPath(_root));
            }
            catch (InvalidDataException ex)
            {
                result.Warnings.Add(ex.Message);
                return;
            }
            if (shared == null)
            {
                result.Warnings.Add("공유 폴더에 색인이 없습니다. 관리자에게 색인 생성(Indexer)을 요청하세요.");
                return;
            }

            local.EmbeddingModel = shared.EmbeddingModel;
            local.Dimension = shared.Dimension;
            foreach (var id in productIds)
            {
                ct.ThrowIfCancellationRequested();
                var remote = shared.Find(id);
                if (remote == null) continue;
                var cached = local.Find(id);
                if (cached != null && cached.Version == remote.Version && cached.Sha256 == remote.Sha256
                    && File.Exists(Path.Combine(IndexDir, cached.File)))
                    continue;

                var fileName = $"{remote.ProductId}.v{remote.Version}.sqlite";
                var target = Path.Combine(IndexDir, fileName);
                Directory.CreateDirectory(IndexDir);
                var tmp = target + ".tmp";
                File.Copy(Path.Combine(KbLayout.IndexDir(_root), remote.File), tmp, true);
                if (!string.Equals(FileHash.Sha256(tmp), remote.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    result.Warnings.Add($"'{id}' 색인 복사본의 해시가 매니페스트와 달라 기존 캐시를 유지합니다.");
                    continue;
                }
                AtomicFile.Replace(tmp, target);

                var entry = new ProductIndexInfo
                {
                    ProductId = remote.ProductId,
                    File = fileName,
                    Version = remote.Version,
                    Sha256 = remote.Sha256,
                    BuiltAtUtc = remote.BuiltAtUtc,
                    FileCount = remote.FileCount,
                    ChunkCount = remote.ChunkCount,
                };
                local.Products = local.Products
                    .Where(p => !string.Equals(p.ProductId, id, StringComparison.OrdinalIgnoreCase))
                    .Concat(new[] { entry })
                    .ToList();
                local.Save(LocalManifestPath);
                if (cached != null && cached.File != fileName) TryDelete(Path.Combine(IndexDir, cached.File));
                result.UpdatedProducts.Add(remote.ProductId);
            }
            local.Save(LocalManifestPath);
        }

        private static bool IsCompleteModel(string dir) =>
            Directory.Exists(dir) && RequiredModelFiles.All(f => File.Exists(Path.Combine(dir, f)));

        private static void CopyAtomic(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var tmp = destination + ".tmp";
            File.Copy(source, tmp, true);
            AtomicFile.Replace(tmp, destination);
        }

        /// <summary>이전 버전 파일은 검색기가 아직 열고 있을 수 있으므로 실패해도 무시한다.</summary>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
