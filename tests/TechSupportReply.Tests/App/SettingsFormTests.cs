using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.App.SettingsUi;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class SettingsFormTests
    {
        private sealed class Host : ISettingsHost
        {
            public Host(string dir) { Secrets = new SecretStore(dir); }
            public AppSettings Settings { get; set; } = new AppSettings();
            public SecretStore Secrets { get; }
            public Func<string, string> GetEnv { get; } = _ => null;
            public AppSettings Applied { get; private set; }
            public void ApplySettings(AppSettings settings) => Applied = settings;
            public Task<string> SyncNowAsync(CancellationToken ct) => Task.FromResult("ok");
            public IndexManifest LoadLocalManifest() => new IndexManifest
            {
                EmbeddingModel = "bge-m3-int8",
                Products = { new ProductIndexInfo { ProductId = "ls-dyna", Version = 3, BuiltAtUtc = new DateTime(2026, 9, 1), FileCount = 4, ChunkCount = 120 } },
            };
            public ILlmProvider CreateLlmForTest(LlmProfile profile, string apiKey) => new FakeLlmProvider().Enqueue("OK");
        }

        [Fact]
        public void Loads_ProfilesAndTabs()
        {
            using (var tmp = new TempDir())
            {
                var host = new Host(tmp.Root);
                host.Settings.Profiles.Add(new LlmProfile { Id = "a", DisplayName = "팀 Claude", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5" });
                Sta.Run(() =>
                {
                    using (var f = new SettingsForm(host))
                    {
                        Assert.Equal(3, f.Tabs.TabPages.Count);
                        Assert.Equal(new[] { "팀 Claude" }, f.ProfileList.Items.Cast<object>().Select(o => o.ToString()));
                    }
                });
            }
        }

        [Fact]
        public void AddPreset_ThenSave_AppliesSettings()
        {
            using (var tmp = new TempDir())
            {
                var host = new Host(tmp.Root);
                Sta.Run(() =>
                {
                    using (var f = new SettingsForm(host))
                    {
                        f.AddPreset(ProfilePreset.Xai);
                        Assert.Single(f.ProfileList.Items);
                        Assert.True(f.TrySave(out var errors), string.Join("\n", errors));
                    }
                });
                Assert.Equal("grok-4", host.Applied.Profiles.Single().Model);
            }
        }

        [Fact]
        public void TrySave_InvalidSettings_ReturnsErrors_AndDoesNotApply()
        {
            using (var tmp = new TempDir())
            {
                var host = new Host(tmp.Root);
                Sta.Run(() =>
                {
                    using (var f = new SettingsForm(host))
                    {
                        f.AddPreset(ProfilePreset.OpenAI);
                        f.AddPreset(ProfilePreset.Xai);
                        f.Editor.Working.Profiles[0].Model = "";
                        Assert.False(f.TrySave(out var errors));
                        Assert.NotEmpty(errors);
                    }
                });
                Assert.Null(host.Applied);
            }
        }
    }
}
