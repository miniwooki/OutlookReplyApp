using System.Collections.Generic;
using System.Linq;
using TechSupportReply.Core.Settings;
using Xunit;

namespace TechSupportReply.Tests.Core.Settings
{
    public class DefaultProfileSeederTests
    {
        private static System.Func<string, string> Env(Dictionary<string, string> d) => n => d.TryGetValue(n, out var v) ? v : null;

        [Fact]
        public void CreatesOneProfilePerAvailableKey_InClaudeOpenAiXaiOrder()
        {
            var s = new AppSettings();
            var added = DefaultProfileSeeder.SeedFromEnvironment(s, Env(new Dictionary<string, string>
            {
                ["ANTHROPIC_API_KEY"] = "a", ["OPENAI_API_KEY"] = "o", ["XAI_API_KEY"] = "x", ["ANTHROPIC_WORKSPACE_ID"] = "wrkspc_1",
            }));

            Assert.True(added);
            Assert.Equal(new[] { LlmProviderKind.Anthropic, LlmProviderKind.OpenAI, LlmProviderKind.OpenAI }, s.Profiles.Select(p => p.Provider));
            Assert.Equal(new[] { "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "XAI_API_KEY" }, s.Profiles.Select(p => p.ApiKeyEnvVar));
            Assert.Equal("wrkspc_1", s.Profiles[0].WorkspaceId);
            Assert.Equal("claude-opus-5", s.Profiles[0].Model);
            Assert.Equal("gpt-5.1", s.Profiles[1].Model);
            Assert.Equal("grok-4", s.Profiles[2].Model);
            Assert.Equal("https://api.x.ai/v1", s.Profiles[2].BaseUrl);
            Assert.Equal(s.Profiles[0].Id, s.DefaultProfileId);
            Assert.Equal(s.Profiles[0].Id, s.ClassifierProfileId);
            Assert.All(s.Profiles, p => Assert.Equal("", p.SecretId));
        }

        [Fact]
        public void SkipsMissingKeys()
        {
            var s = new AppSettings();
            DefaultProfileSeeder.SeedFromEnvironment(s, Env(new Dictionary<string, string> { ["XAI_API_KEY"] = "x" }));
            Assert.Single(s.Profiles);
            Assert.Equal(s.Profiles[0].Id, s.DefaultProfileId);
        }

        [Fact]
        public void DoesNothing_WhenProfilesExistOrNoKeys()
        {
            var existing = new AppSettings();
            existing.Profiles.Add(new LlmProfile { DisplayName = "mine" });
            Assert.False(DefaultProfileSeeder.SeedFromEnvironment(existing, _ => "k"));
            Assert.Single(existing.Profiles);

            var empty = new AppSettings();
            Assert.False(DefaultProfileSeeder.SeedFromEnvironment(empty, _ => null));
            Assert.Empty(empty.Profiles);
        }

        [Fact]
        public void Presets_HaveDistinctIds()
        {
            Assert.NotEqual(ProfilePresets.Create(ProfilePreset.Claude).Id, ProfilePresets.Create(ProfilePreset.Claude).Id);
            Assert.Equal("xAI Grok", ProfilePresets.Create(ProfilePreset.Xai).DisplayName);
        }
    }
}
