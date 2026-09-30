using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Serialization;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.App.SettingsUi
{
    /// <summary>
    /// 설정 대화상자의 편집 상태. 원본 설정의 사본(Working)을 고치고, [저장] 시 Commit에서
    /// 새 API 키를 DPAPI에 쓰고 삭제된 프로필의 키를 지운다. 취소하면 아무것도 바뀌지 않는다.
    /// </summary>
    public sealed class SettingsEditor
    {
        private readonly SecretStore _secrets;
        private readonly Dictionary<string, string> _pendingKeys = new Dictionary<string, string>();
        private readonly HashSet<string> _removedSecretIds = new HashSet<string>();

        public SettingsEditor(AppSettings current, SecretStore secrets)
        {
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            Working = Clone(current ?? new AppSettings());
        }

        public AppSettings Working { get; }

        public static AppSettings Clone(AppSettings s) =>
            JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonDefaults.Options), JsonDefaults.Options);

        public LlmProfile AddProfile(ProfilePreset preset)
        {
            var p = ProfilePresets.Create(preset);
            p.DisplayName = UniqueName(p.DisplayName);
            Working.Profiles.Add(p);
            if (Working.FindProfile(Working.DefaultProfileId) == null) Working.DefaultProfileId = p.Id;
            if (Working.FindProfile(Working.ClassifierProfileId) == null) Working.ClassifierProfileId = p.Id;
            return p;
        }

        public void RemoveProfile(string id)
        {
            var p = Working.FindProfile(id);
            if (p == null) return;
            Working.Profiles.Remove(p);
            _pendingKeys.Remove(id);
            if (!string.IsNullOrEmpty(p.SecretId)) _removedSecretIds.Add(p.SecretId);
            var fallback = Working.Profiles.FirstOrDefault()?.Id ?? "";
            if (Working.DefaultProfileId == id) Working.DefaultProfileId = fallback;
            if (Working.ClassifierProfileId == id) Working.ClassifierProfileId = fallback;
        }

        public void SetPendingKey(string profileId, string key) => _pendingKeys[profileId] = (key ?? "").Trim();

        public bool HasPendingKey(string profileId) => _pendingKeys.TryGetValue(profileId, out var k) && k.Length > 0;

        public bool HasStoredKey(LlmProfile p)
        {
            if (p == null || string.IsNullOrEmpty(p.SecretId)) return false;
            try { return !string.IsNullOrEmpty(_secrets.Get(p.SecretId)); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is CryptographicException) { return false; }
        }

        /// <summary>[연결 테스트]용 키: 환경 변수(값이 있을 때) → 입력 중인 키 → 저장된 키.</summary>
        public string ResolveKey(LlmProfile p, Func<string, string> getEnv)
        {
            var fromEnv = LlmProviderFactory.ResolveEnvKey(p, getEnv);
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
            if (HasPendingKey(p.Id)) return _pendingKeys[p.Id];
            return HasStoredKey(p) ? _secrets.Get(p.SecretId) : null;
        }

        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            foreach (var p in Working.Profiles)
            {
                var name = string.IsNullOrWhiteSpace(p.DisplayName) ? "(이름 없음)" : p.DisplayName.Trim();
                if (string.IsNullOrWhiteSpace(p.DisplayName)) errors.Add("프로필 이름이 비어 있습니다.");
                if (string.IsNullOrWhiteSpace(p.Model)) errors.Add($"'{name}': 모델명을 입력하세요.");
                else if (p.Provider == LlmProviderKind.Anthropic)
                {
                    try { AnthropicProvider.EnsureSupportedModel(p.Model.Trim()); }
                    catch (ArgumentException ex) { errors.Add($"'{name}': {ex.Message}"); }
                }
                if (!string.IsNullOrWhiteSpace(p.BaseUrl)
                    && (!Uri.TryCreate(p.BaseUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                    errors.Add($"'{name}': Base URL 형식이 올바르지 않습니다(예: https://api.x.ai/v1).");
                if (p.MaxTokens < 256 || p.MaxTokens > 128000) errors.Add($"'{name}': 최대 토큰은 256~128000 사이여야 합니다.");
            }
            foreach (var dup in Working.Profiles.GroupBy(p => (p.DisplayName ?? "").Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0 && g.Count() > 1))
                errors.Add($"프로필 이름 '{dup.Key}'이(가) 중복됩니다.");
            if (Working.ReferenceTopK < 1 || Working.ReferenceTopK > 30) errors.Add("근거 문서 수는 1~30 사이여야 합니다.");
            if (Working.StyleExampleTopK < 0 || Working.StyleExampleTopK > 10) errors.Add("문체 예시 수는 0~10 사이여야 합니다.");
            return errors;
        }

        public AppSettings Commit()
        {
            foreach (var kv in _pendingKeys)
            {
                var p = Working.FindProfile(kv.Key);
                if (p == null || kv.Value.Length == 0) continue;
                if (string.IsNullOrEmpty(p.SecretId)) p.SecretId = SecretStore.NewId();
                _secrets.Set(p.SecretId, kv.Value);
            }
            foreach (var id in _removedSecretIds) _secrets.Remove(id);
            _pendingKeys.Clear();
            _removedSecretIds.Clear();
            return Clone(Working);
        }

        private string UniqueName(string baseName)
        {
            var name = baseName;
            for (int i = 2; Working.Profiles.Any(p => string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)); i++)
                name = baseName + " " + i;
            return name;
        }
    }
}
