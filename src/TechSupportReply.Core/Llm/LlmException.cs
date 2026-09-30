using System;

namespace TechSupportReply.Core.Llm
{
    public enum LlmErrorKind
    {
        Authentication,
        PermissionDenied,
        NotFound,
        RateLimited,
        Server,
        Network,
        Refusal,
        InvalidRequest,
        WorkspaceRequired,
        NotConfigured,
        Unknown,
    }

    public sealed class LlmException : Exception
    {
        public LlmException(LlmErrorKind kind, string detail, Exception inner = null)
            : base(detail, inner)
        {
            Kind = kind;
        }

        public LlmErrorKind Kind { get; }

        public string UserMessage => Kind switch
        {
            LlmErrorKind.Authentication => "API 키가 올바르지 않거나 등록되지 않았습니다. 설정에서 키를 확인하세요.",
            LlmErrorKind.PermissionDenied => "이 API 키로는 해당 모델을 사용할 권한이 없습니다.",
            LlmErrorKind.NotFound => "모델 이름을 찾을 수 없습니다. 프로필의 모델명을 확인하세요.",
            LlmErrorKind.RateLimited => "요청 한도를 초과했습니다. 잠시 후 다시 시도하세요.",
            LlmErrorKind.Server => "LLM 서비스에 일시적인 오류가 발생했습니다. 잠시 후 다시 시도하세요.",
            LlmErrorKind.Network => "LLM 서비스에 연결할 수 없습니다. 네트워크 연결을 확인하세요.",
            LlmErrorKind.Refusal => "모델이 이 요청에 대한 응답을 거절했습니다. 내용을 수정하거나 다른 프로필로 시도하세요.",
            LlmErrorKind.WorkspaceRequired => "이 API 키는 워크스페이스에 속해 있지 않아 Workspace ID가 필요합니다. [설정] → LLM 프로필에서 Workspace ID(wrkspc_로 시작)를 입력하거나 워크스페이스에 속한 키를 사용하세요.",
            LlmErrorKind.NotConfigured => Message,
            LlmErrorKind.InvalidRequest => "요청 형식 오류: " + Message,
            _ => "알 수 없는 오류: " + Message,
        };
    }
}
