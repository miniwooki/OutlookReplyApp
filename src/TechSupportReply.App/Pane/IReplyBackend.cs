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
        /// <summary>
        /// 프로필에 쓸 수 있는 API 키가 있는지(DPAPI 저장 키 또는 값이 있는 ApiKeyEnvVar). 로컬 파일과 레지스트리를 읽으므로
        /// 프레젠터는 백그라운드에서 호출한다.
        /// </summary>
        bool HasUsableKey(LlmProfile profile);
        /// <summary>작업창에서 사용자가 고른 답변 프로필을 settings.json(LastProfileId)에 기억한다.</summary>
        void SaveLastProfile(string profileId);
    }
}
