using TechSupportReply.Core.Products;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Products
{
    public class KeywordClassifierTests
    {
        private readonly KeywordClassifier _classifier = new KeywordClassifier(ProductCatalog.CreateDefault());

        [Fact]
        public void LsDynaMail_ClassifiedAsLsDyna_WithHighConfidence()
        {
            var r = _classifier.Classify(Mails.Create("LS-DYNA 접촉 관통 문의",
                "*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE 사용 중 d3hsp에 경고가 있습니다."));
            Assert.Equal("ls-dyna", r.ProductId);
            Assert.Equal(ClassificationSource.Keyword, r.Source);
            Assert.True(r.Confidence > 0.5, $"confidence={r.Confidence}");
            Assert.Contains("ls-dyna", r.Reason);
        }

        [Fact]
        public void FluentMail_ClassifiedAsFluent()
        {
            Assert.Equal("ansys-fluent", _classifier.Classify(Mails.Create("Fluent 계산 수렴 문제", "residual이 줄지 않습니다")).ProductId);
        }

        [Fact]
        public void LicenseMail_ClassifiedAsCommon()
        {
            Assert.Equal(ProductCatalog.CommonId, _classifier.Classify(Mails.Create("ansyslmd 라이선스 서버 연결 실패", "")).ProductId);
        }

        [Fact]
        public void NoKeywords_DefaultsToCommon()
        {
            var r = _classifier.Classify(Mails.Create("문의드립니다", "The solver works fluently."));
            Assert.Equal(ProductCatalog.CommonId, r.ProductId);
            Assert.Equal(ClassificationSource.Default, r.Source);
            Assert.Equal(0, r.Confidence);
        }

        [Fact]
        public void KeywordBoundaries_AreRespected()
        {
            Assert.Equal("ls-dyna", _classifier.Classify(Mails.Create("R13 문의", "*MAT_ELASTIC 카드 질문")).ProductId);
            Assert.Equal("ls-dyna", _classifier.Classify(Mails.Create("LS-DYNA R13", "")).ProductId);
        }

        [Fact]
        public void Subject_OutweighsBody()
        {
            Assert.Equal("ansys-electronics", _classifier.Classify(Mails.Create("HFSS 포트 설정", "fluent 사용자는 아닙니다")).ProductId);
        }

        [Fact]
        public void AttachmentNames_AreScored()
        {
            Assert.Equal("ls-dyna", _classifier.Classify(Mails.Create("해석 오류", "첨부 확인 부탁드립니다", "d3hsp", "messag")).ProductId);
        }

        [Fact]
        public void TwoProductsTie_LowersConfidence()
        {
            Assert.True(_classifier.Classify(Mails.Create("Fluent와 CFX 비교", "")).Confidence < 0.5);
        }
    }
}
