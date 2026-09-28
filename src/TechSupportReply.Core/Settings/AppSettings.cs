using System;
using System.Collections.Generic;
using System.Linq;

namespace TechSupportReply.Core.Settings
{
    public enum LlmProviderKind
    {
        Anthropic,
        OpenAI,
    }

    public sealed class LlmProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string DisplayName { get; set; } = "";
        public LlmProviderKind Provider { get; set; }
        public string Model { get; set; } = "";
        /// <summary>OpenAI 호환 엔드포인트(선택). 비어 있으면 공급자 기본값.</summary>
        public string BaseUrl { get; set; } = "";
        public int MaxTokens { get; set; } = 16000;
        /// <summary>low | medium | high | max (Anthropic만 사용).</summary>
        public string Effort { get; set; } = "medium";
        public string SecretId { get; set; } = "";
    }

    public sealed class UserProfile
    {
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Company { get; set; } = "KOSTECH";
        public string Tone { get; set; } = "정중하고 간결한 기술지원 어조";
    }

    public sealed class AppSettings
    {
        public int SchemaVersion { get; set; } = 1;
        public UserProfile User { get; set; } = new UserProfile();
        public string RagRoot { get; set; } = "";
        public List<LlmProfile> Profiles { get; set; } = new List<LlmProfile>();
        public string DefaultProfileId { get; set; } = "";
        public string ClassifierProfileId { get; set; } = "";
        public int ReferenceTopK { get; set; } = 8;
        public int StyleExampleTopK { get; set; } = 3;
        public int MaxMailChars { get; set; } = 30000;

        public LlmProfile FindProfile(string id) => Profiles.FirstOrDefault(p => p.Id == id);
    }
}
