namespace TechSupportReply.Core.Settings
{
    public enum ProfilePreset
    {
        Claude,
        OpenAI,
        Xai,
    }

    /// <summary>설정 화면의 [추가]와 환경 변수 시드가 함께 쓰는 기본 프로필. 모델명은 2026-09-28 실제 호출로 확인했다.</summary>
    public static class ProfilePresets
    {
        public const string ClaudeModel = "claude-opus-5";
        public const string OpenAiModel = "gpt-5.1";
        public const string XaiModel = "grok-4";
        public const string XaiBaseUrl = "https://api.x.ai/v1";
        public const string AnthropicKeyEnv = "ANTHROPIC_API_KEY";
        public const string OpenAiKeyEnv = "OPENAI_API_KEY";
        public const string XaiKeyEnv = "XAI_API_KEY";
        public const string AnthropicWorkspaceEnv = "ANTHROPIC_WORKSPACE_ID";

        public static LlmProfile Create(ProfilePreset preset)
        {
            switch (preset)
            {
                case ProfilePreset.Claude:
                    return new LlmProfile { DisplayName = "Claude", Provider = LlmProviderKind.Anthropic, Model = ClaudeModel, Effort = "medium" };
                case ProfilePreset.OpenAI:
                    return new LlmProfile { DisplayName = "OpenAI", Provider = LlmProviderKind.OpenAI, Model = OpenAiModel };
                default:
                    return new LlmProfile { DisplayName = "xAI Grok", Provider = LlmProviderKind.OpenAI, Model = XaiModel, BaseUrl = XaiBaseUrl };
            }
        }
    }
}
