using System.Collections.Generic;
using System.Linq;
using System.Text;
using TechSupportReply.Core.Knowledge;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Models;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Prompting
{
    public sealed class PromptInput
    {
        public MailSnapshot Mail { get; set; } = new MailSnapshot();
        public ProductDefinition Product { get; set; }
        /// <summary>제품 폴더의 _prompt.md 전문(없으면 빈 문자열).</summary>
        public string ProductGuide { get; set; } = "";
        public UserProfile User { get; set; } = new UserProfile();
        public RetrievalResult Retrieval { get; set; } = new RetrievalResult();
        public string ExtraInstruction { get; set; } = "";
        public int MaxMailChars { get; set; } = 30000;
        public int MaxAttachmentChars { get; set; } = 20000;
    }

    public sealed class BuiltPrompt
    {
        public LlmRequest Request { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// 답변 생성 요청을 만든다. 공통 지침 + 제품 지침은 제품별로 고정되어 프롬프트 캐싱 대상이 되고,
    /// 작성자 정보는 시스템 뒷부분, 문체 예시·근거·고객 메일은 사용자 메시지에 들어간다.
    /// </summary>
    public static class PromptBuilder
    {
        private const string BaseRules =
            "당신은 KOSTECH 기술지원 엔지니어입니다. CAE 소프트웨어(LS-DYNA, Ansys 등) 고객의 기술지원 메일에 보낼 회신 본문을 작성합니다.\n\n" +
            "규칙:\n" +
            "1. 고객 메일이 쓰인 언어(한국어 또는 영어)로 답변합니다.\n" +
            "2. 참고 자료와 일반적으로 확립된 사용법에 근거해 답변하고, 참고 자료에 없는 키워드 카드·옵션 이름이나 수치를 지어내지 않습니다.\n" +
            "3. 확신할 수 없는 내용은 해당 문장 끝에 [확인 필요]를 붙입니다.\n" +
            "4. 원인을 판단할 정보가 부족하면 필요한 자료(예: d3hsp, messag, 입력 키워드 파일, 솔버 버전, 라이선스 로그, 오류 화면)를 구체적으로 요청합니다.\n" +
            "5. 인사말로 시작하고, 문제 요약 → 원인/설명 → 조치 방법(단계별) → 추가로 필요한 정보 순서로 쓰며, 맺음말로 끝냅니다.\n" +
            "6. 서명과 이전 메일 인용은 쓰지 않습니다(메일 프로그램이 붙입니다). 마크다운 없이 일반 텍스트 문단으로 본문만 출력합니다.\n" +
            "7. 참고 자료의 출처 표기([출처: ...])는 본문에 옮기지 않습니다.";

        public static BuiltPrompt Build(PromptInput input)
        {
            var result = new BuiltPrompt();
            var product = input.Product;
            var guide = string.IsNullOrWhiteSpace(input.ProductGuide) ? "(별도 지침 없음)" : input.ProductGuide.Trim();

            var request = new LlmRequest
            {
                CachedSystem = BaseRules + $"\n\n## 제품 지침 ({product?.DisplayName ?? "공통"})\n" + guide,
                System = BuildAuthor(input.User),
            };

            var msg = new StringBuilder();
            var retrieval = input.Retrieval ?? new RetrievalResult();
            if (retrieval.StyleExamples.Count > 0)
            {
                msg.Append("## 문체 예시(과거 답변)\n아래는 팀의 과거 답변입니다. 내용이 아니라 어조·구성만 참고하세요.\n\n");
                int n = 1;
                foreach (var ex in retrieval.StyleExamples) msg.Append($"### 예시 {n++}\n{ex.Text.Trim()}\n\n");
            }

            msg.Append("## 참고 자료\n");
            if (retrieval.References.Count == 0)
            {
                msg.Append("(참고 자료 없음: 일반적으로 확립된 지식으로만 답변하고, 불확실한 내용에는 [확인 필요]를 붙이세요.)\n\n");
            }
            else
            {
                foreach (var r in retrieval.References) msg.Append($"[출처: {r.Citation}]\n{r.Text.Trim()}\n\n");
            }

            var mail = input.Mail ?? new MailSnapshot();
            var body = Truncate(Normalize(mail.Body), input.MaxMailChars, "메일 본문", result.Warnings);
            msg.Append("## 고객 메일\n");
            msg.Append($"제목: {mail.Subject}\n");
            msg.Append($"보낸 사람: {mail.SenderName} <{mail.SenderEmail}>\n");
            if (mail.ReceivedAt != default) msg.Append($"받은 날짜: {mail.ReceivedAt:yyyy-MM-dd HH:mm}\n");
            if (mail.AttachmentNames != null && mail.AttachmentNames.Count > 0) msg.Append($"첨부: {string.Join(", ", mail.AttachmentNames)}\n");
            msg.Append($"\n{body}\n\n");

            var attachment = Normalize(mail.AttachmentText);
            if (attachment.Length > 0)
                msg.Append("## 첨부 텍스트\n").Append(Truncate(attachment, input.MaxAttachmentChars, "첨부 텍스트", result.Warnings)).Append("\n\n");

            if (!string.IsNullOrWhiteSpace(input.ExtraInstruction))
                msg.Append("## 추가 지시\n").Append(input.ExtraInstruction.Trim()).Append("\n\n");

            msg.Append("위 고객 메일에 대한 회신 본문을 작성하세요.");
            request.Messages.Add(LlmMessage.User(msg.ToString()));
            result.Request = request;
            return result;
        }

        private static string BuildAuthor(UserProfile user)
        {
            user = user ?? new UserProfile();
            var who = string.Join(" ", new[] { user.Company, user.Name, user.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return $"답변 작성자: {(who.Length > 0 ? who : "KOSTECH 기술지원팀")}\n어조: {(string.IsNullOrWhiteSpace(user.Tone) ? "정중하고 간결하게" : user.Tone)}";
        }

        private static string Normalize(string text) => (text ?? "").Replace("\r\n", "\n").Trim();

        private static string Truncate(string text, int max, string label, List<string> warnings)
        {
            if (max <= 0 || text.Length <= max) return text;
            int omitted = text.Length - max;
            warnings.Add($"{label}이(가) 길어 뒤쪽 {omitted}자를 생략하고 생성했습니다.");
            return text.Substring(0, max) + $"\n[... 이하 {omitted}자 생략 ...]";
        }
    }
}
