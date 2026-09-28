using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TechSupportReply.Core.Models;

namespace TechSupportReply.Core.Products
{
    public sealed class KeywordScore
    {
        public string ProductId { get; set; } = "";
        public double Score { get; set; }
        public List<string> MatchedKeywords { get; set; } = new List<string>();
    }

    /// <summary>제목(×3)·첨부파일명(×2)·본문(×1)에서 제품 키워드 출현 횟수(필드당 최대 5)를 점수화한다.</summary>
    public sealed class KeywordClassifier
    {
        private const double SubjectWeight = 3;
        private const double AttachmentWeight = 2;
        private const double BodyWeight = 1;
        private const int MaxHitsPerField = 5;

        private readonly ProductCatalog _catalog;
        private readonly Dictionary<string, Regex> _patterns = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        public KeywordClassifier(ProductCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public IReadOnlyList<KeywordScore> Score(MailSnapshot mail)
        {
            var subject = mail.Subject ?? "";
            var body = (mail.Body ?? "") + "\n" + (mail.AttachmentText ?? "");
            var attachments = string.Join("\n", mail.AttachmentNames ?? new List<string>());
            var scores = new List<KeywordScore>();
            foreach (var product in _catalog.Products)
            {
                var score = new KeywordScore { ProductId = product.Id };
                foreach (var keyword in product.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)))
                {
                    var re = PatternFor(keyword);
                    double hits = Hits(re, subject) * SubjectWeight + Hits(re, attachments) * AttachmentWeight + Hits(re, body) * BodyWeight;
                    if (hits <= 0) continue;
                    score.Score += hits;
                    score.MatchedKeywords.Add(keyword);
                }
                if (score.Score > 0) scores.Add(score);
            }
            return scores
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.ProductId == ProductCatalog.CommonId ? 1 : 0)
                .ToList();
        }

        public ClassificationResult Classify(MailSnapshot mail)
        {
            var scores = Score(mail);
            if (scores.Count == 0)
            {
                return new ClassificationResult
                {
                    ProductId = ProductCatalog.CommonId,
                    Confidence = 0,
                    Source = ClassificationSource.Default,
                    Reason = "일치하는 제품 키워드가 없어 공통으로 분류했습니다.",
                };
            }
            var top = scores[0];
            var second = scores.Count > 1 ? scores[1].Score : 0;
            return new ClassificationResult
            {
                ProductId = top.ProductId,
                Confidence = Math.Round(top.Score / (top.Score + second + 1.0), 2),
                Source = ClassificationSource.Keyword,
                Reason = "키워드: " + string.Join(", ", top.MatchedKeywords.Take(5)),
            };
        }

        private Regex PatternFor(string keyword)
        {
            if (_patterns.TryGetValue(keyword, out var cached)) return cached;
            var k = keyword.Trim().ToLowerInvariant();
            var prefix = IsAsciiAlnum(k[0]) ? "(?<![a-z0-9])" : "";
            var suffix = IsAsciiAlnum(k[k.Length - 1]) ? "(?![a-z0-9])" : "";
            var re = new Regex(prefix + Regex.Escape(k) + suffix, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _patterns[keyword] = re;
            return re;
        }

        private static bool IsAsciiAlnum(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

        private static int Hits(Regex re, string text) => Math.Min(MaxHitsPerField, re.Matches(text).Count);
    }
}
