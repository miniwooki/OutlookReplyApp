using System;
using System.Collections.Generic;
using System.IO;

namespace TechSupportReply.Rag.Loaders
{
    public sealed class DocumentLoaderRegistry
    {
        private readonly Dictionary<string, IDocumentLoader> _byExtension =
            new Dictionary<string, IDocumentLoader>(StringComparer.OrdinalIgnoreCase);

        public DocumentLoaderRegistry(IEnumerable<IDocumentLoader> loaders)
        {
            foreach (var loader in loaders)
                foreach (var ext in loader.Extensions)
                    _byExtension[ext] = loader;
        }

        public static DocumentLoaderRegistry CreateDefault() => new DocumentLoaderRegistry(new IDocumentLoader[]
        {
            new TextLoader(),
            new MarkdownLoader(),
            new CsvLoader(),
            new PdfLoader(),
            new DocxLoader(),
            new XlsxLoader(),
            new EmailLoader(),
        });

        public IReadOnlyCollection<string> SupportedExtensions => _byExtension.Keys;

        public bool CanLoad(string path) => _byExtension.ContainsKey(Path.GetExtension(path ?? "") ?? "");

        public LoadedDocument Load(string path)
        {
            if (!_byExtension.TryGetValue(Path.GetExtension(path ?? "") ?? "", out var loader))
                throw new DocumentLoadException(path, "지원하지 않는 파일 형식입니다.");
            try
            {
                return loader.Load(path);
            }
            catch (DocumentLoadException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DocumentLoadException(path, "파일을 읽을 수 없습니다: " + ex.Message, ex);
            }
        }
    }
}
