using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Rag.Loaders;
using TechSupportReply.Rag.Search;
using TechSupportReply.Rag.Store;

namespace TechSupportReply.Rag.Indexing
{
    public sealed class SkippedFile
    {
        public string RelativePath { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public sealed class IndexBuildReport
    {
        public string ProductId { get; set; } = "";
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Removed { get; set; }
        public int Unchanged { get; set; }
        /// <summary>빌드 후 색인에 들어 있는 파일 수.</summary>
        public int FileCount { get; set; }
        public int ChunkCount { get; set; }
        public List<SkippedFile> Skipped { get; } = new List<SkippedFile>();
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// 제품 폴더 1개를 색인 파일 1개로 만든다. 크기·수정 시각이 같으면 건너뛰고, 다르면 SHA-256으로
    /// 실제 변경을 확인한 뒤 다시 임베딩한다. 읽을 수 없는 파일은 사유와 함께 건너뛴다.
    /// </summary>
    public sealed class IndexBuilder
    {
        public const string MetaProductId = "product_id";
        public const string MetaBuiltAt = "built_at";

        private readonly DocumentLoaderRegistry _loaders;
        private readonly Chunker _chunker;
        private readonly IEmbedder _embedder;

        public IndexBuilder(DocumentLoaderRegistry loaders, Chunker chunker, IEmbedder embedder)
        {
            _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));
            _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
            _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        }

        public IndexBuildReport Build(string productId, string productDir, string indexPath, bool full, IProgress<string> progress, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var report = new IndexBuildReport { ProductId = productId };
            if (full || ModelDiffers(indexPath)) DeleteIndex(indexPath);

            using (var store = SqliteIndexStore.Create(indexPath))
            {
                store.SetMeta(HybridRetriever.MetaEmbeddingModel, _embedder.ModelId);
                store.SetMeta(HybridRetriever.MetaDimension, _embedder.Dimension.ToString(CultureInfo.InvariantCulture));
                store.SetMeta(MetaProductId, productId);

                var indexed = store.GetFiles().ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
                var current = ScanFiles(productDir);
                int n = 0;
                foreach (var file in current)
                {
                    ct.ThrowIfCancellationRequested();
                    n++;
                    var relative = Relative(productDir, file.FullName);
                    progress?.Report($"[{n}/{current.Count}] {relative}");
                    indexed.TryGetValue(relative, out var existing);
                    indexed.Remove(relative);
                    var mtime = file.LastWriteTimeUtc;
                    if (existing != null && existing.Size == file.Length && existing.MtimeUtc == mtime)
                    {
                        report.Unchanged++;
                        continue;
                    }

                    string hash;
                    try
                    {
                        hash = FileHash.Sha256(file.FullName);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        report.Skipped.Add(new SkippedFile { RelativePath = relative, Reason = "파일을 열 수 없습니다: " + ex.Message });
                        continue;
                    }
                    if (existing != null && existing.Hash == hash)
                    {
                        store.TouchFile(relative, file.Length, mtime);
                        report.Unchanged++;
                        continue;
                    }

                    List<NewChunk> chunks;
                    try
                    {
                        chunks = MakeChunks(file.FullName, KbLayout.DocTypeFor(relative), ct);
                    }
                    catch (DocumentLoadException ex)
                    {
                        report.Skipped.Add(new SkippedFile { RelativePath = relative, Reason = ex.Reason });
                        if (existing != null) store.RemoveFile(relative);
                        continue;
                    }
                    store.UpsertFile(relative, file.Length, mtime, hash, chunks);
                    if (existing == null) report.Added++;
                    else report.Updated++;
                }

                foreach (var gone in indexed.Keys)
                {
                    store.RemoveFile(gone);
                    report.Removed++;
                }

                store.SetMeta(MetaBuiltAt, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                report.FileCount = store.GetFiles().Count;
                report.ChunkCount = store.ChunkCount;
            }
            report.Elapsed = sw.Elapsed;
            return report;
        }

        private List<NewChunk> MakeChunks(string path, DocType docType, CancellationToken ct)
        {
            var doc = _loaders.Load(path);
            var result = new List<NewChunk>();
            foreach (var draft in _chunker.Chunk(doc))
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new NewChunk
                {
                    DocType = docType,
                    Title = draft.Title,
                    Page = draft.Page,
                    Text = draft.Text,
                    Vector = _embedder.Embed(draft.Title + "\n" + draft.Text),
                });
            }
            return result;
        }

        private List<FileInfo> ScanFiles(string productDir)
        {
            if (!Directory.Exists(productDir)) return new List<FileInfo>();
            return new DirectoryInfo(productDir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !KbLayout.IsIgnored(Relative(productDir, f.FullName)) && _loaders.CanLoad(f.FullName))
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool ModelDiffers(string indexPath)
        {
            if (!File.Exists(indexPath)) return false;
            using (var store = SqliteIndexStore.OpenReadOnly(indexPath))
            {
                return store.GetMeta(HybridRetriever.MetaEmbeddingModel) != _embedder.ModelId
                    || store.GetMeta(HybridRetriever.MetaDimension) != _embedder.Dimension.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void DeleteIndex(string indexPath)
        {
            if (File.Exists(indexPath)) File.Delete(indexPath);
        }

        private static string Relative(string root, string fullPath)
        {
            var baseDir = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(fullPath);
            return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) ? full.Substring(baseDir.Length) : Path.GetFileName(full);
        }
    }
}
