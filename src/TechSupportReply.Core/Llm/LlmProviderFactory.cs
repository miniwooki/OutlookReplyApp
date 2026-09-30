using System;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Llm
{
    public sealed class LlmProviderFactory
    {
        private readonly SecretStore _secrets;
        private readonly Func<string, string> _getEnv;
        private readonly Func<LlmProfile, string, ILlmProvider> _create;

        public LlmProviderFactory(SecretStore secrets, Func<string, string> getEnv = null, Func<LlmProfile, string, ILlmProvider> create = null)
        {
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            _getEnv = getEnv ?? EnvironmentVariables.Get;
            _create = create ?? Create;
        }

        /// <summary>환경 변수(지정된 경우) → DPAPI 비밀 순서로 API 키를 찾는다. 없으면 null.</summary>
        public string ResolveApiKey(LlmProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var fromEnv = ResolveEnvKey(profile, _getEnv);
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
            var stored = _secrets.Get(profile.SecretId);
            return string.IsNullOrWhiteSpace(stored) ? null : stored;
        }

        /// <summary>프로필의 환경 변수에서 API 키를 읽는다(앞뒤 공백 제거). 변수명이 없거나 값이 비면 null.</summary>
        public static string ResolveEnvKey(LlmProfile profile, Func<string, string> getEnv)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(profile.ApiKeyEnvVar)) return null;
            var fromEnv = getEnv?.Invoke(profile.ApiKeyEnvVar.Trim());
            return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
        }

        public ILlmProvider Create(LlmProfile profile)
        {
            var key = ResolveApiKey(profile);
            if (key == null)
            {
                var hint = string.IsNullOrWhiteSpace(profile.ApiKeyEnvVar)
                    ? "[설정]에서 API 키를 입력하세요."
                    : $"환경 변수 {profile.ApiKeyEnvVar.Trim()}에 값이 없습니다. 환경 변수를 설정한 뒤 Outlook을 다시 시작하거나 [설정]에서 키를 직접 입력하세요.";
                throw new LlmException(LlmErrorKind.NotConfigured, $"'{profile.DisplayName}' 프로필에 API 키가 없습니다. {hint}");
            }
            return _create(profile, key);
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
