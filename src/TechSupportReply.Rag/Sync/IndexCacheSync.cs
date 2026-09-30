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
        private volatile IndexManifest _local;
        private bool _localLoaded;

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

        /// <summary>
        /// 로컬 캐시 매니페스트. 검색 스레드가 동기화 중에도 파일을 읽지 않도록 메모리 사본을 돌려준다.
        /// </summary>
        public IndexManifest LoadLocalManifest()
        {
            if (!_localLoaded)
            {
                lock (_lock)
                {
                    if (!_localLoaded)
                    {
                        _local = ReadLocalManifestFile();
                        _localLoaded = true;
                    }
                }
            }
            return _local;
        }

        /// <summary>
        /// 잠금 없이 캐시 매니페스트 파일을 바로 읽는다(설정 창 등 UI 스레드용). Sync가 공유 폴더 응답을 기다리며 잠금을 오래 쥐고 있어도 막히지 않는다.
        /// 매니페스트는 임시 파일에 쓴 뒤 교체(AtomicFile)하므로 동기화 중에도 이전 또는 새 내용 전체를 읽는다. 형식 오류면 null.
        /// </summary>
        public IndexManifest ReadLocalManifestUnlocked() => ReadLocalManifestFile();

        private IndexManifest ReadLocalManifestFile()
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

        /// <summary>로컬 매니페스트가 가리키지 않는 캐시 색인 파일을 지운다(열려 있어 실패하면 다음에 다시 시도).</summary>
        public void CleanupUnreferenced()
        {
            if (!Directory.Exists(IndexDir)) return;
            var keep = new HashSet<string>((LoadLocalManifest()?.Products ?? new List<ProductIndexInfo>()).Select(p => p.File),
                StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(IndexDir, "*.sqlite"))
                if (!keep.Contains(Path.GetFileName(file))) TryDelete(file);
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
                var local = ReadLocalManifestFile() ?? new IndexManifest();
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
                _local = ReadLocalManifestFile();
                _localLoaded = true;
                CleanupUnreferenced();
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

                // 파일명에 버전과 해시를 넣어, 내용이 다르면 항상 새 파일이 되게 한다.
                // 검색기가 열어 둔 이전 파일을 덮어쓰면 그 연결이 깨지기 때문이다.
                var fileName = CacheFileName(remote);
                var target = Path.Combine(IndexDir, fileName);
                Directory.CreateDirectory(IndexDir);
                if (!File.Exists(target) || !string.Equals(FileHash.Sha256(target), remote.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    var tmp = target + ".tmp";
                    File.Copy(Path.Combine(KbLayout.IndexDir(_root), remote.File), tmp, true);
                    if (!string.Equals(FileHash.Sha256(tmp), remote.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(tmp);
                        result.Warnings.Add($"'{id}' 색인 복사본의 해시가 매니페스트와 달라 기존 캐시를 유지합니다.");
                        continue;
                    }
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);
                }

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
                _local = local;
                result.UpdatedProducts.Add(remote.ProductId);
            }
            local.Save(LocalManifestPath);
        }

        private static string CacheFileName(ProductIndexInfo remote)
        {
            var hash = (remote.Sha256 ?? "").ToLowerInvariant();
            return $"{remote.ProductId}.v{remote.Version}.{(hash.Length >= 12 ? hash.Substring(0, 12) : hash)}.sqlite";
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
