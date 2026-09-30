using System;

namespace TechSupportReply.Core.Settings
{
    /// <summary>처음 실행할 때(프로필 0개) 값이 있는 API 키 환경 변수마다 프로필을 만든다. 키 자체는 저장하지 않는다.</summary>
    public static class DefaultProfileSeeder
    {
        public static bool SeedFromEnvironment(AppSettings settings, Func<string, string> getEnv)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (getEnv == null) throw new ArgumentNullException(nameof(getEnv));
            if (settings.Profiles.Count > 0) return false;

            bool Has(string name) => !string.IsNullOrWhiteSpace(getEnv(name));

            if (Has(ProfilePresets.AnthropicKeyEnv))
            {
                var p = ProfilePresets.Create(ProfilePreset.Claude);
                p.ApiKeyEnvVar = ProfilePresets.AnthropicKeyEnv;
                p.WorkspaceId = (getEnv(ProfilePresets.AnthropicWorkspaceEnv) ?? "").Trim();
                settings.Profiles.Add(p);
            }
            if (Has(ProfilePresets.OpenAiKeyEnv))
            {
                var p = ProfilePresets.Create(ProfilePreset.OpenAI);
                p.ApiKeyEnvVar = ProfilePresets.OpenAiKeyEnv;
                settings.Profiles.Add(p);
            }
            if (Has(ProfilePresets.XaiKeyEnv))
            {
                var p = ProfilePresets.Create(ProfilePreset.Xai);
                p.ApiKeyEnvVar = ProfilePresets.XaiKeyEnv;
                settings.Profiles.Add(p);
            }
            if (settings.Profiles.Count == 0) return false;

            settings.DefaultProfileId = settings.Profiles[0].Id;
            settings.ClassifierProfileId = settings.Profiles[0].Id;
            return true;
        }
    }
}
