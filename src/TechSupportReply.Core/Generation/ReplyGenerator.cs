using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Knowledge;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Models;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Prompting;
using TechSupportReply.Core.Settings;
using TechSupportReply.Core.Text;

namespace TechSupportReply.Core.Generation
{
    public sealed class ReplyRequest
    {
        public MailSnapshot Mail { get; set; } = new MailSnapshot();
        /// <summary>사용자가 확인·변경한 제품 id.</summary>
        public string ProductId { get; set; } = ProductCatalog.CommonId;
        public string ExtraInstruction { get; set; } = "";
        public bool UseRag { get; set; } = true;
    }

    public sealed class ReplyResult
    {
        public string Text { get; set; } = "";
        public string ProductId { get; set; } = "";
        public List<KnowledgeChunk> References { get; } = new List<KnowledgeChunk>();
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>검색 → 프롬프트 구성 → 스트리밍 생성을 조율한다. 메일은 발송하지 않는다.</summary>
    public sealed class ReplyGenerator
    {
        private const int QueryBodyChars = 1000;

        private readonly ProductCatalog _catalog;
        private readonly IKnowledgeRetriever _retriever;
        private readonly Func<ProductDefinition, string> _guideLoader;
        private readonly AppSettings _settings;

        public ReplyGenerator(ProductCatalog catalog, IKnowledgeRetriever retriever, Func<ProductDefinition, string> productGuideLoader, AppSettings settings)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _retriever = retriever;
            _guideLoader = productGuideLoader ?? (p => "");
            _settings = settings ?? new AppSettings();
        }

        public async Task<ReplyResult> GenerateAsync(ReplyRequest request, ILlmProvider llm, Action<string> onDelta, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (llm == null) throw new ArgumentNullException(nameof(llm));
            var result = new ReplyResult();

            var product = _catalog.Find(request.ProductId);
            if (product == null)
            {
                result.Warnings.Add($"알 수 없는 제품 '{request.ProductId}' 대신 공통으로 생성합니다.");
                product = _catalog.Common;
            }
            result.ProductId = product.Id;

            var retrieval = new RetrievalResult();
            if (request.UseRag && _retriever != null)
            {
                try
                {
                    retrieval = await _retriever.RetrieveAsync(product.Id, BuildQuery(request.Mail), _settings.ReferenceTopK, _settings.StyleExampleTopK, ct)
                                    .ConfigureAwait(false) ?? new RetrievalResult();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"지식 검색에 실패해 RAG 없이 생성합니다: {ex.Message}");
                    retrieval = new RetrievalResult();
                }
            }
            result.Warnings.AddRange(retrieval.Warnings);
            result.References.AddRange(retrieval.References);

            var prompt = PromptBuilder.Build(new PromptInput
            {
                Mail = request.Mail,
                Product = product,
                ProductGuide = LoadGuide(product, result.Warnings),
                User = _settings.User,
                Retrieval = retrieval,
                ExtraInstruction = request.ExtraInstruction,
                MaxMailChars = _settings.MaxMailChars,
            });
            result.Warnings.AddRange(prompt.Warnings);

            result.Text = await llm.StreamAsync(prompt.Request, onDelta, ct).ConfigureAwait(false);
            return result;
        }

        internal static string BuildQuery(MailSnapshot mail)
        {
            var latest = EmailTextCleaner.Split(mail?.Body).Latest;
            if (latest.Length > QueryBodyChars) latest = latest.Substring(0, QueryBodyChars);
            return ((mail?.Subject ?? "") + "\n" + latest).Trim();
        }

        private string LoadGuide(ProductDefinition product, List<string> warnings)
        {
            try
            {
                return _guideLoader(product) ?? "";
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                warnings.Add($"제품 지침(_prompt.md)을 읽지 못해 공통 지침만 사용합니다: {ex.Message}");
                return "";
            }
        }
    }
}
