using System.IO;
using System.Linq;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Settings
{
    public class SettingsStoreTests
    {
        [Fact]
        public void Load_WhenMissing_ReturnsDefaults()
        {
            using (var tmp = new TempDir())
            {
                var s = new SettingsStore(tmp.Root).Load();
                Assert.Empty(s.Profiles);
                Assert.Equal(8, s.ReferenceTopK);
                Assert.Equal("KOSTECH", s.User.Company);
            }
        }

        [Fact]
        public void SaveThenLoad_RoundTripsProfiles_WithEnumAsString()
        {
            using (var tmp = new TempDir())
            {
                var store = new SettingsStore(tmp.Root);
                var settings = new AppSettings { RagRoot = @"\\server\KB" };
                var profile = new LlmProfile { DisplayName = "Claude 팀 키", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", SecretId = "s1" };
                settings.Profiles.Add(profile);
                settings.DefaultProfileId = profile.Id;
                store.Save(settings);

                Assert.Contains("\"Anthropic\"", File.ReadAllText(store.FilePath));
                Assert.Contains("Claude 팀 키", File.ReadAllText(store.FilePath));

                var loaded = store.Load();
                Assert.Equal(@"\\server\KB", loaded.RagRoot);
                var p = loaded.FindProfile(profile.Id);
                Assert.Equal("claude-opus-5", p.Model);
                Assert.Equal(LlmProviderKind.Anthropic, p.Provider);
            }
        }

        [Fact]
        public void Load_WhenCorrupted_BacksUpAndReturnsDefaults()
        {
            using (var tmp = new TempDir())
            {
                var store = new SettingsStore(tmp.Root);
                File.WriteAllText(store.FilePath, "{ not json");
                var s = store.Load();
                Assert.Empty(s.Profiles);
                Assert.True(Directory.GetFiles(tmp.Root, "settings.json.bad-*").Any());
            }
        }
    }
}
