using System;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Indexing;

namespace TechSupportReply.App.SettingsUi
{
    public interface ISettingsHost
    {
        AppSettings Settings { get; }
        SecretStore Secrets { get; }
        Func<string, string> GetEnv { get; }
        void ApplySettings(AppSettings settings);
        Task<string> SyncNowAsync(CancellationToken ct);
        IndexManifest LoadLocalManifest();
        ILlmProvider CreateLlmForTest(LlmProfile profile, string apiKey);
    }
}
