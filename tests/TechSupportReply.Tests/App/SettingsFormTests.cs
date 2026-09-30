using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
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
            public Func<string, string> GetEnv { get; set; } = _ => null;
            public Func<LlmProfile, string, CancellationToken, Task<IReadOnlyList<ModelListing>>> ListModels { get; set; } =
                (p, k, ct) => Task.FromResult<IReadOnlyList<ModelListing>>(new ModelListing[0]);
            public List<(LlmProfile Profile, string Key)> ListCalls { get; } = new List<(LlmProfile Profile, string Key)>();

            public Task<IReadOnlyList<ModelListing>> ListModelsAsync(LlmProfile profile, string apiKey, CancellationToken ct)
            {
                lock (ListCalls) ListCalls.Add((profile, apiKey));
                return ListModels(profile, apiKey, ct);
            }
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

        /// <summary>STA 스레드에서 비동기 처리기의 UI 연속 작업이 돌도록 메시지를 펌프한다.</summary>
        private static void Pump(Task task)
        {
            var sw = Stopwatch.StartNew();
            while (!task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            Assert.True(task.IsCompleted, "작업이 10초 안에 끝나지 않았습니다.");
            task.GetAwaiter().GetResult();
        }

        private static Host HostWith(string dir, LlmProfile profile, Func<string, string> env)
        {
            var host = new Host(dir) { GetEnv = env };
            host.Settings.Profiles.Add(profile);
            return host;
        }

        [Fact]
        public void LoadModels_FillsDropdownOffUiThread_KeepsTypedModel_AndSavesChoice()
        {
            using (var tmp = new TempDir())
            {
                var host = HostWith(tmp.Root,
                    new LlmProfile { Id = "x", DisplayName = "xAI Grok", Provider = LlmProviderKind.OpenAI, Model = "my-model", BaseUrl = "https://api.x.ai/v1", ApiKeyEnvVar = "XAI_API_KEY" },
                    n => n == "XAI_API_KEY" ? "xai-k" : null);
                int listThread = 0;
                host.ListModels = (p, k, ct) =>
                {
                    listThread = Environment.CurrentManagedThreadId;
                    return Task.FromResult<IReadOnlyList<ModelListing>>(new[] { new ModelListing("grok-4", "grok-4", null), new ModelListing("grok-3", "grok-3", null) });
                };
                Sta.Run(() =>
                {
                    using (var f = new SettingsForm(host))
                    {
                        Pump(f.LoadModelsAsync());

                        Assert.NotEqual(Environment.CurrentManagedThreadId, listThread);
                        Assert.Equal(new[] { "grok-4", "grok-3" }, f.ModelCombo.Items.Cast<object>().Select(o => o.ToString()));
                        Assert.Equal("my-model", f.ModelCombo.Text);
                        Assert.Contains("2개", f.ModelsResultLabel.Text);
                        Assert.Contains("목록에 없습니다", f.ModelsResultLabel.Text);

                        f.ModelCombo.Text = "grok-4";   // 사용자가 목록에서 고른 것과 같다
                        Assert.True(f.TrySave(out var errors), string.Join("\n", errors));
                    }
                });
                var call = host.ListCalls.Single();
                Assert.Equal("xai-k", call.Key);
                Assert.Equal("https://api.x.ai/v1", call.Profile.BaseUrl);
                Assert.Equal("grok-4", host.Applied.Profiles.Single().Model);
            }
        }

        [Fact]
        public void LoadModels_WithoutKey_ShowsHint_AndDoesNotCallHost()
        {
            using (var tmp = new TempDir())
            {
                var host = HostWith(tmp.Root, new LlmProfile { Id = "c", DisplayName = "Claude", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5" }, _ => null);
                Sta.Run(() =>
                {
                    using (var f = new SettingsForm(host))
                    {
                        Pump(f.LoadModelsAsync());
                        Assert.Contains("API 키가 없습니다", f.ModelsResultLabel.Text);
                    }
                });
                Assert.Empty(host.ListCalls);
            }
        }

        [Fact]
        public void LoadModels_ProviderError_ShowsUserMessage()
        {
            using (var tmp = new TempDir())
            {
                var host = HostWith(tmp.Root,
                    new LlmProfile { Id = "c", DisplayName = "Claude", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY" },
                    n => n == "ANTHROPIC_API_KEY" ? "sk-ant" : null);
                host.ListModels = (p, k, ct) => Task.FromException<IReadOnlyList<ModelListing>>(new LlmException(LlmErrorKind.WorkspaceRequired, "not scoped"));
                Sta.Run(() =>
                {
                    using (var f = new SettingsForm(host))
                    {
                        Pump(f.LoadModelsAsync());
                        Assert.Equal(new LlmException(LlmErrorKind.WorkspaceRequired, "x").UserMessage, f.ModelsResultLabel.Text);
                    }
                });
            }
        }

        [Fact]
        public void LoadModels_Timeout_ShowsTimeoutMessage()
        {
            using (var tmp = new TempDir())
            {
                var host = HostWith(tmp.Root,
                    new LlmProfile { Id = "o", DisplayName = "OpenAI", Provider = LlmProviderKind.OpenAI, Model = "gpt-5.1", ApiKeyEnvVar = "OPENAI_API_KEY" },
                    n => n == "OPENAI_API_KEY" ? "sk-o" : null);
                host.ListModels = async (p, k, ct) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return null;
                };
                Sta.Run(() =>
                {
                    using (var f = new SettingsForm(host))
                    {
                        f.ModelListTimeout = TimeSpan.FromMilliseconds(100);
                        Pump(f.LoadModelsAsync());
                        Assert.Contains("시간", f.ModelsResultLabel.Text);
                    }
                });
            }
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
