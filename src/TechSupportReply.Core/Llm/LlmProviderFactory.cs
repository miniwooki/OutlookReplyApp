using System;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Llm
{
    public sealed class LlmProviderFactory
    {
        private readonly SecretStore _secrets;

        public LlmProviderFactory(SecretStore secrets)
        {
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        }

        public ILlmProvider Create(LlmProfile profile)
        {
            var key = _secrets.Get(profile.SecretId);
            if (string.IsNullOrWhiteSpace(key))
                throw new LlmException(LlmErrorKind.Authentication, $"'{profile.DisplayName}' 프로필에 API 키가 등록되지 않았습니다.");
            return Create(profile, key);
        }

        public static ILlmProvider Create(LlmProfile profile, string apiKey)
        {
            switch (profile.Provider)
            {
                case LlmProviderKind.Anthropic: return new AnthropicProvider(profile, apiKey);
                case LlmProviderKind.OpenAI: return new OpenAiProvider(profile, apiKey);
                default: throw new NotSupportedException($"지원하지 않는 공급자: {profile.Provider}");
            }
        }
    }
}
