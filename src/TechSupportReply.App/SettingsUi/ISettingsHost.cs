using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Diagnostics;
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
        /// <summary>애드인 공용 로그. 같은 파일에 쓰는 로그 인스턴스를 하나로 유지해 잠금을 공유한다.</summary>
        FileLog Log { get; }
        void ApplySettings(AppSettings settings);
        Task<string> SyncNowAsync(CancellationToken ct);
        /// <summary>캐시 색인 상태. UI 스레드에서 부르므로 동기화가 끝나기를 기다리지 않아야 한다.</summary>
        IndexManifest LoadLocalManifest();
        ILlmProvider CreateLlmForTest(LlmProfile profile, string apiKey);
        /// <summary>[모델 목록 불러오기]. 네트워크 호출이므로 UI 스레드에서 기다리지 않는다. 실패하면 LlmException.</summary>
        Task<IReadOnlyList<ModelListing>> ListModelsAsync(LlmProfile profile, string apiKey, CancellationToken ct);
    }
}
