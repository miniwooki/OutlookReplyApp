using System.Collections.Generic;

namespace TechSupportReply.Core.Llm
{
    public enum LlmRole
    {
        User,
        Assistant,
    }

    public sealed class LlmMessage
    {
        public LlmRole Role { get; set; }
        public string Content { get; set; } = "";

        public static LlmMessage User(string content) => new LlmMessage { Role = LlmRole.User, Content = content };
        public static LlmMessage Assistant(string content) => new LlmMessage { Role = LlmRole.Assistant, Content = content };
    }

    public sealed class LlmRequest
    {
        /// <summary>요청마다 바뀌지 않는 시스템 프롬프트 앞부분(프롬프트 캐싱 대상). 비어 있으면 안 된다.</summary>
        public string CachedSystem { get; set; } = "";
        /// <summary>요청마다 바뀔 수 있는 시스템 프롬프트 뒷부분.</summary>
        public string System { get; set; } = "";
        public List<LlmMessage> Messages { get; } = new List<LlmMessage>();
        public int MaxTokens { get; set; } = 16000;
        /// <summary>low | medium | high | max. null이면 프로필 값을 사용한다.</summary>
        public string Effort { get; set; }
        /// <summary>설정 시 해당 JSON 스키마를 따르는 JSON만 출력(구조화 출력).</summary>
        public string JsonSchema { get; set; }
        public string SchemaName { get; set; } = "result";
    }
}
