using System;
using System.Collections.Generic;
using System.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace TechSupportReply.Rag.Loaders
{
    /// <summary>PDF를 페이지 단위 섹션으로 읽는다. 암호·스캔(텍스트 없음)·손상 PDF는 사유와 함께 거부한다.</summary>
    public sealed class PdfLoader : IDocumentLoader
    {
        public IReadOnlyCollection<string> Extensions { get; } = new[] { ".pdf" };

        public LoadedDocument Load(string path)
        {
            var title = Path.GetFileNameWithoutExtension(path);
            var doc = new LoadedDocument { Path = path, Kind = DocKind.Pdf, Title = title };
            try
            {
                using (var pdf = PdfDocument.Open(path))
                {
                    foreach (var page in pdf.GetPages())
                    {
                        var text = Normalize(ContentOrderTextExtractor.GetText(page));
                        if (text.Length == 0) continue;
                        doc.Sections.Add(new LoadedSection { Title = title, Page = page.Number, Text = text });
                    }
                }
            }
            catch (PdfDocumentEncryptedException ex)
            {
                throw new DocumentLoadException(path, "암호로 보호된 PDF입니다.", ex);
            }
            catch (Exception ex) when (!(ex is DocumentLoadException) && !(ex is IOException) && !(ex is UnauthorizedAccessException))
            {
                throw new DocumentLoadException(path, "PDF를 읽을 수 없습니다: " + ex.Message, ex);
            }
            if (doc.Sections.Count == 0)
                throw new DocumentLoadException(path, "텍스트가 없는 PDF입니다(스캔 이미지로 추정).");
            return doc;
        }

        private static string Normalize(string text) =>
            (text ?? "").Replace("\r\n", "\n").Replace('\u0000', ' ').Trim();
    }
}
