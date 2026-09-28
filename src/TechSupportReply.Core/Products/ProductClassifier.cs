using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Models;
using TechSupportReply.Core.Text;

namespace TechSupportReply.Core.Products
{
    /// <summary>LLM 구조화 출력으로 제품군을 분류하고, 실패·시간 초과 시 키워드 결과로 대체한다.</summary>
    public sealed class ProductClassifier
    {
        private const int MaxBodyChars = 4000;
        private readonly ProductCatalog _catalog;
        private readonly KeywordClassifier _keyword;
        private readonly TimeSpan _llmTimeout;

        public ProductClassifier(ProductCatalog catalog, TimeSpan? llmTimeout = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _keyword = new KeywordClassifier(catalog);
            _llmTimeout = llmTimeout ?? TimeSpan.FromSeconds(30);
        }

        public ClassificationResult ClassifyByKeywords(MailSnapshot mail) => _keyword.Classify(mail);

        public async Task<ClassificationResult> ClassifyAsync(MailSnapshot mail, ILlmProvider llm, CancellationToken ct)
        {
            var keywordResult = _keyword.Classify(mail);
            if (llm == null) return keywordResult;

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_llmTimeout);
                try
                {
                    var json = await llm.CompleteAsync(BuildRequest(mail, _keyword.Score(mail)), timeout.Token).ConfigureAwait(false);
                    return Parse(json) ?? WithNote(keywordResult, "LLM 분류 결과를 해석할 수 없어 키워드 결과를 사용합니다.");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return WithNote(keywordResult, "LLM 분류 시간 초과로 키워드 결과를 사용합니다.");
                }
                catch (LlmException ex)
                {
                    return WithNote(keywordResult, $"LLM 분류 실패({ex.UserMessage}) — 키워드 결과를 사용합니다.");
                }
            }
        }

        internal LlmRequest BuildRequest(MailSnapshot mail, IReadOnlyList<KeywordScore> hints)
        {
            var productLines = string.Join("\n", _catalog.Products.Select(p => $"- {p.Id}: {p.DisplayName}"));
            var hintText = hints.Count == 0
                ? "(없음)"
                : string.Join(", ", hints.Take(3).Select(h => $"{h.ProductId}({h.Score:0.#})"));
            var body = EmailTextCleaner.Split(mail.Body).Latest;
            if (body.Length > MaxBodyChars) body = body.Substring(0, MaxBodyChars) + "\n[...생략...]";

            var request = new LlmRequest
            {
                CachedSystem =
                    "당신은 CAE 소프트웨어(LS-DYNA, Ansys 등) 기술지원 메일을 제품군별로 분류합니다. " +
                    "메일이 어떤 제품에 대한 문의인지 아래 목록에서 하나만 고르세요. " +
                    "라이선스·설치·계정처럼 특정 제품의 해석 기능과 무관한 문의는 _common을 고르세요. " +
                    "confidence는 0~1 사이 확신도, reason은 한국어 한 문장입니다.\n\n제품 목록:\n" + productLines,
                MaxTokens = 2000,
                Effort = "low",
                JsonSchema = BuildSchema(),
                SchemaName = "product_classification",
            };
            request.Messages.Add(LlmMessage.User(
                $"키워드 힌트: {hintText}\n\n제목: {mail.Subject}\n첨부: {string.Join(", ", mail.AttachmentNames ?? new List<string>())}\n\n본문:\n{body}"));
            return request;
        }

        internal ClassificationResult Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json.Substring(start, end - start + 1)))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("productId", out var idElement) || idElement.ValueKind != JsonValueKind.String) return null;
                    var product = _catalog.Find(idElement.GetString());
                    if (product == null) return null;
                    double confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.5;
                    if (confidence > 1 && confidence <= 100) confidence /= 100;
                    confidence = Math.Max(0, Math.Min(1, confidence));
                    var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : "";
                    return new ClassificationResult
                    {
                        ProductId = product.Id,
                        Confidence = Math.Round(confidence, 2),
                        Reason = reason,
                        Source = ClassificationSource.Llm,
                    };
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private string BuildSchema() => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["productId"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = _catalog.Products.Select(p => p.Id).ToArray() },
                ["confidence"] = new Dictionary<string, object> { ["type"] = "number" },
                ["reason"] = new Dictionary<string, object> { ["type"] = "string" },
            },
            ["required"] = new[] { "productId", "confidence", "reason" },
            ["additionalProperties"] = false,
        });

        private static ClassificationResult WithNote(ClassificationResult source, string note) => new ClassificationResult
        {
            ProductId = source.ProductId,
            Confidence = source.Confidence,
            Source = source.Source,
            Reason = source.Reason + " / " + note,
        };
    }
}
