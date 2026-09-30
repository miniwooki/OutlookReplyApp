using System;
using System.Collections.Generic;
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
        /// <summary>[모델 목록 불러오기]. 네트워크 호출이므로 UI 스레드에서 기다리지 않는다. 실패하면 LlmException.</summary>
        Task<IReadOnlyList<ModelListing>> ListModelsAsync(LlmProfile profile, string apiKey, CancellationToken ct);
    }
}
