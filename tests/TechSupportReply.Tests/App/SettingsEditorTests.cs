using System.Linq;
using TechSupportReply.App.SettingsUi;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class SettingsEditorTests
    {
        [Fact]
        public void Working_IsIsolatedCopy()
        {
            using (var tmp = new TempDir())
            {
                var original = new AppSettings();
                var editor = new SettingsEditor(original, new SecretStore(tmp.Root));
                editor.AddProfile(ProfilePreset.Claude);
                editor.Working.User.Name = "변경";
                Assert.Empty(original.Profiles);
                Assert.Equal("", original.User.Name);
            }
        }

        [Fact]
        public void AddProfile_SetsDefaults_AndUniqueNames()
        {
            using (var tmp = new TempDir())
            {
                var editor = new SettingsEditor(new AppSettings(), new SecretStore(tmp.Root));
                var a = editor.AddProfile(ProfilePreset.Claude);
                var b = editor.AddProfile(ProfilePreset.Claude);
                Assert.Equal(a.Id, editor.Working.DefaultProfileId);
                Assert.Equal(a.Id, editor.Working.ClassifierProfileId);
                Assert.Equal("Claude", a.DisplayName);
                Assert.Equal("Claude 2", b.DisplayName);
            }
        }

        [Fact]
        public void Commit_StoresPendingKey_WithNewSecretId()
        {
            using (var tmp = new TempDir())
            {
                var secrets = new SecretStore(tmp.Root);
                var editor = new SettingsEditor(new AppSettings(), secrets);
                var p = editor.AddProfile(ProfilePreset.OpenAI);
                editor.SetPendingKey(p.Id, " sk-new ");
                Assert.True(editor.HasPendingKey(p.Id));
                var saved = editor.Commit();
                var sp = saved.FindProfile(p.Id);
                Assert.NotEqual("", sp.SecretId);
                Assert.Equal("sk-new", secrets.Get(sp.SecretId));
                Assert.True(editor.HasStoredKey(sp));
            }
        }

        [Fact]
        public void RemoveProfile_DeletesSecretOnCommit_AndFixesDefaults()
        {
            using (var tmp = new TempDir())
            {
                var secrets = new SecretStore(tmp.Root);
                secrets.Set("s1", "old");
                var current = new AppSettings();
                current.Profiles.Add(new LlmProfile { Id = "a", DisplayName = "A", Provider = LlmProviderKind.OpenAI, Model = "m", SecretId = "s1" });
                current.Profiles.Add(new LlmProfile { Id = "b", DisplayName = "B", Provider = LlmProviderKind.OpenAI, Model = "m" });
                current.DefaultProfileId = "a";
                current.ClassifierProfileId = "a";
                var editor = new SettingsEditor(current, secrets);

                editor.RemoveProfile("a");
                Assert.Equal("old", secrets.Get("s1"));
                var saved = editor.Commit();
                Assert.Null(secrets.Get("s1"));
                Assert.Equal("b", saved.DefaultProfileId);
                Assert.Equal("b", saved.ClassifierProfileId);
            }
        }

        [Fact]
        public void ResolveKey_Priority_EnvThenPendingThenStored()
        {
            using (var tmp = new TempDir())
            {
                var secrets = new SecretStore(tmp.Root);
                secrets.Set("s1", "stored");
                var current = new AppSettings();
                current.Profiles.Add(new LlmProfile { Id = "a", DisplayName = "A", SecretId = "s1", ApiKeyEnvVar = "K" });
                var editor = new SettingsEditor(current, secrets);
                var p = editor.Working.FindProfile("a");

                Assert.Equal("env", editor.ResolveKey(p, _ => "env"));
                Assert.Equal("stored", editor.ResolveKey(p, _ => null));
                editor.SetPendingKey("a", "pending");
                Assert.Equal("pending", editor.ResolveKey(p, _ => null));
            }
        }

        [Fact]
        public void Validate_ReportsProblems()
        {
            using (var tmp = new TempDir())
            {
                var editor = new SettingsEditor(new AppSettings(), new SecretStore(tmp.Root));
                var a = editor.AddProfile(ProfilePreset.Claude);
                a.Model = "claude-3-5-sonnet-latest";
                var b = editor.AddProfile(ProfilePreset.OpenAI);
                b.Model = "";
                b.BaseUrl = "not a url";
                b.MaxTokens = 10;
                var c = editor.AddProfile(ProfilePreset.Xai);
                c.DisplayName = a.DisplayName;
                editor.Working.ReferenceTopK = 0;

                var errors = editor.Validate();
                Assert.Contains(errors, e => e.Contains("claude-3-5-sonnet-latest"));
                Assert.Contains(errors, e => e.Contains("모델명"));
                Assert.Contains(errors, e => e.Contains("Base URL"));
                Assert.Contains(errors, e => e.Contains("최대 토큰"));
                Assert.Contains(errors, e => e.Contains("중복"));
                Assert.Contains(errors, e => e.Contains("근거"));
            }
        }

        [Fact]
        public void Validate_DefaultPresetsAreValid()
        {
            using (var tmp = new TempDir())
            {
                var editor = new SettingsEditor(new AppSettings(), new SecretStore(tmp.Root));
                editor.AddProfile(ProfilePreset.Claude);
                editor.AddProfile(ProfilePreset.OpenAI);
                editor.AddProfile(ProfilePreset.Xai);
                Assert.Empty(editor.Validate());
            }
        }

        [Fact]
        public void HasStoredKey_CorruptSecretsFile_ReturnsFalse()
        {
            using (var tmp = new TempDir())
            {
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(tmp.Root, SecretStore.FileName), new byte[] { 1, 2, 3, 4, 5 });
                var editor = new SettingsEditor(new AppSettings(), new SecretStore(tmp.Root));
                Assert.False(editor.HasStoredKey(new LlmProfile { SecretId = "s1" }));
            }
        }
    }
}
