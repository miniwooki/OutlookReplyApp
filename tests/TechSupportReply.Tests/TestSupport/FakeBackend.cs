using System;
using System.Collections.Generic;
using System.Threading;
using TechSupportReply.App.Hosting;
using TechSupportReply.App.Pane;
using TechSupportReply.Core.Diagnostics;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Tests.TestSupport
{
    internal sealed class FakeBackend : IReplyBackend
    {
        public FakeBackend(FakeLlmProvider llm, FakeRetriever retriever = null)
        {
            Llm = llm;
            Retriever = retriever ?? new FakeRetriever();
            var profile = new LlmProfile { Id = "p1", DisplayName = "테스트", Provider = LlmProviderKind.OpenAI, Model = "m" };
            Settings = new AppSettings { DefaultProfileId = "p1", ClassifierProfileId = "p1" };
            Settings.Profiles.Add(profile);
            Log = new FileLog(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tsr-tests", Guid.NewGuid().ToString("N"), "logs"));
        }

        public FakeLlmProvider Llm { get; }
        public FakeRetriever Retriever { get; }
        public AppSettings Settings { get; }
        public FileLog Log { get; }
        public string LogText => System.IO.File.Exists(Log.CurrentPath) ? System.IO.File.ReadAllText(Log.CurrentPath) : "";
        public Exception CreateLlmThrows { get; set; }
        /// <summary>설정하면 GetSession이 이 이벤트가 신호될 때까지 막힌다(느린 UNC 흉내).</summary>
        public ManualResetEventSlim SessionGate { get; set; }
        public int GetSessionThreadId { get; private set; }
        /// <summary>GetSession에 들어오면(게이트에서 기다리기 전) 신호된다.</summary>
        public ManualResetEventSlim SessionEntered { get; } = new ManualResetEventSlim(false);
        public List<string> WarningsForSession { get; } = new List<string>();

        public KnowledgeSession GetSession()
        {
            GetSessionThreadId = Environment.CurrentManagedThreadId;
            SessionEntered.Set();
            SessionGate?.Wait(TimeSpan.FromSeconds(10));
            return new KnowledgeSession(ProductCatalog.CreateDefault(), Retriever, p => "", WarningsForSession);
        }

        /// <summary>null이면 모든 프로필에 키가 있다고 본다.</summary>
        public Func<LlmProfile, bool> KeyCheck { get; set; }
        public Exception SaveLastProfileThrows { get; set; }
        public List<string> SavedLastProfileIds { get; } = new List<string>();
        public List<string> CreatedProfileIds { get; } = new List<string>();

        public bool HasUsableKey(LlmProfile profile) => KeyCheck == null || KeyCheck(profile);

        public void SaveLastProfile(string profileId)
        {
            if (SaveLastProfileThrows != null) throw SaveLastProfileThrows;
            SavedLastProfileIds.Add(profileId);
            Settings.LastProfileId = profileId;
        }

        public ILlmProvider CreateLlm(string profileId)
        {
            CreatedProfileIds.Add(profileId);
            if (CreateLlmThrows != null) throw CreateLlmThrows;
            return Llm;
        }
    }
}
