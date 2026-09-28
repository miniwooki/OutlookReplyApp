using System.Linq;
using TechSupportReply.Core.Knowledge;
using TechSupportReply.Core.Prompting;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Prompting
{
    public class PromptBuilderTests
    {
        private static readonly ProductDefinition Dyna = ProductCatalog.CreateDefault().Find("ls-dyna");

        private static PromptInput Input(string body = "초기 관통 경고가 납니다.", RetrievalResult retrieval = null) => new PromptInput
        {
            Mail = Mails.Create("접촉 문의", body, "d3hsp"),
            Product = Dyna,
            ProductGuide = "키워드 카드 이름은 대문자로 쓴다.",
            User = new UserProfile { Name = "홍길동", Title = "선임연구원", Company = "KOSTECH", Tone = "정중하게" },
            Retrieval = retrieval ?? new RetrievalResult(),
        };

        private static string UserMessage(BuiltPrompt p) => Assert.Single(p.Request.Messages).Content;

        [Fact]
        public void Build_CachedSystemContainsRulesAndProductGuide()
        {
            var p = PromptBuilder.Build(Input());
            Assert.Contains("[확인 필요]", p.Request.CachedSystem);
            Assert.Contains("지어내지", p.Request.CachedSystem);
            Assert.Contains("d3hsp", p.Request.CachedSystem);
            Assert.Contains("LS-DYNA", p.Request.CachedSystem);
            Assert.Contains("키워드 카드 이름은 대문자로 쓴다.", p.Request.CachedSystem);
        }

        [Fact]
        public void Build_SameProduct_CachedSystemIdenticalAcrossMails()
        {
            var a = PromptBuilder.Build(Input("첫 메일"));
            var b = PromptBuilder.Build(Input("두 번째 메일"));
            Assert.Equal(a.Request.CachedSystem, b.Request.CachedSystem);
            Assert.DoesNotContain("첫 메일", a.Request.CachedSystem);
        }

        [Fact]
        public void Build_ReferencesHaveCitations()
        {
            var r = new RetrievalResult();
            r.References.Add(new KnowledgeChunk { ProductId = "ls-dyna", SourceFile = @"manuals\Keyword.pdf", Page = 12, Text = "IGNORE=1 설명" });
            var msg = UserMessage(PromptBuilder.Build(Input(retrieval: r)));
            Assert.Contains("[출처: Keyword.pdf p.12]", msg);
            Assert.Contains("IGNORE=1 설명", msg);
            Assert.True(msg.IndexOf("## 참고 자료") < msg.IndexOf("## 고객 메일"));
        }

        [Fact]
        public void Build_NoReferences_AddsNoEvidenceNotice()
        {
            var msg = UserMessage(PromptBuilder.Build(Input()));
            Assert.Contains("참고 자료 없음", msg);
        }

        [Fact]
        public void Build_StyleExamplesIncluded()
        {
            var r = new RetrievalResult();
            r.StyleExamples.Add(new KnowledgeChunk { SourceFile = @"replies\a.eml", Text = "안녕하세요. 과거 답변 문체", IsReplyExample = true });
            var msg = UserMessage(PromptBuilder.Build(Input(retrieval: r)));
            Assert.Contains("## 문체 예시", msg);
            Assert.Contains("과거 답변 문체", msg);
            Assert.True(msg.IndexOf("## 문체 예시") < msg.IndexOf("## 참고 자료"));
        }

        [Fact]
        public void Build_ExtraInstructionIncluded()
        {
            var input = Input();
            input.ExtraInstruction = "영어로 답변해 주세요";
            var msg = UserMessage(PromptBuilder.Build(input));
            Assert.Contains("## 추가 지시", msg);
            Assert.Contains("영어로 답변해 주세요", msg);
            Assert.DoesNotContain("## 추가 지시", UserMessage(PromptBuilder.Build(Input())));
        }

        [Fact]
        public void Build_LongBody_TruncatesWithMarkerAndWarning()
        {
            var input = Input(new string('가', 150));
            input.MaxMailChars = 100;
            var p = PromptBuilder.Build(input);
            var msg = UserMessage(p);
            Assert.Contains(new string('가', 100), msg);
            Assert.DoesNotContain(new string('가', 101), msg);
            Assert.Contains("[... 이하 50자 생략 ...]", msg);
            Assert.Contains(p.Warnings, w => w.Contains("메일 본문") && w.Contains("50자"));
        }

        [Fact]
        public void Build_LongAttachment_TruncatesWithWarning()
        {
            var input = Input();
            input.Mail.AttachmentText = new string('L', 300);
            input.MaxAttachmentChars = 200;
            var p = PromptBuilder.Build(input);
            var msg = UserMessage(p);
            Assert.Contains("## 첨부 텍스트", msg);
            Assert.Contains("[... 이하 100자 생략 ...]", msg);
            Assert.Contains(p.Warnings, w => w.Contains("첨부") && w.Contains("100자"));
        }

        [Fact]
        public void Build_UserProfileInSystem()
        {
            var p = PromptBuilder.Build(Input());
            Assert.Contains("홍길동", p.Request.System);
            Assert.Contains("선임연구원", p.Request.System);
            Assert.Contains("정중하게", p.Request.System);
            Assert.DoesNotContain("홍길동", p.Request.CachedSystem);
            Assert.Empty(p.Warnings);
            Assert.Contains("d3hsp", UserMessage(p));
        }
    }
}
