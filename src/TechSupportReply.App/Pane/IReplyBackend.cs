using TechSupportReply.App.Hosting;
using TechSupportReply.Core.Diagnostics;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.App.Pane
{
    public interface IReplyBackend
    {
        AppSettings Settings { get; }
        FileLog Log { get; }
        /// <summary>공유 폴더에 접근하므로 오래 걸릴 수 있다. UI 스레드에서 호출하지 않는다.</summary>
        KnowledgeSession GetSession();
        /// <summary>프로필을 찾지 못하면 기본 프로필, 그다음 첫 프로필을 쓴다. 키가 없으면 LlmException(NotConfigured).</summary>
        ILlmProvider CreateLlm(string profileId);
    }
}
