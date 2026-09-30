# Plan B — Outlook VSTO 애드인·설정 UI·배포 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Plan A의 헤드리스 코어(Core·Rag)를 Outlook Classic에 붙이는 VSTO 애드인(리본·작업창·설정 대화상자)을 만들고, 빌드·설치(수동 등록/ClickOnce)·문서까지 완성한다.

**Architecture:** COM에 의존하지 않는 로직은 모두 테스트 가능한 SDK 스타일 프로젝트에 둔다. Core(netstandard2.0)에는 순수 로직(HTML 회신 합성, 로그, 환경 변수 키, 연결 테스트)을 추가하고, 새 `TechSupportReply.App`(net48, WinForms)에는 조립 루트(`AddInServices`), 작업창 프레젠터(`ReplyPanePresenter`)와 WinForms 화면, 설정 편집기, 네이티브 DLL 선로딩, 리본 XML을 둔다. 구형 csproj인 VSTO 프로젝트 `TechSupportReply.AddIn`은 얇게 유지한다. 여기에는 `ThisAddIn`, 리본 콜백, `MailItem`→`MailSnapshot` 추출, `ReplyAll` 초안 작성, 작업창 관리만 둔다.

**Tech Stack:** .NET Framework 4.8, VSTO 4.0(Outlook 16.0 x64), WinForms, 기존 Anthropic 12.x·OpenAI 2.x SDK, xUnit 2.9(net48), MSBuild(VS 2022 Community + "Office/SharePoint 개발" 워크로드), ClickOnce.

**Spec:** `docs/superpowers/specs/2026-09-28-outlook-techsupport-autoreply-design.md` (특히 §1 아키텍처, §3.1 설정, §3.5 회신 초안, §3.6 오류·로그, §4 기술 위험, §6 문서)

**실행 순서:** Task 1 → 2 → … → 12 → **15** → 13 → 14. Task 15(API 모델 목록 불러오기·작업창 프로필 선택, 2026-09-30 추가)는 Task 1~11이 만든 코드를 고치므로 Task 12 다음, 문서(Task 13)와 E2E(Task 14) 전에 실행한다. 문서 안의 Task 번호는 바꾸지 않았다.

## Global Constraints

- 대상: Outlook Classic M365/2016+ **x64**, .NET Framework 4.8, VSTO Runtime 10.0(설치 확인됨: 10.0.60910).
- 자동 발송 금지. `MailItem.ReplyAll()` 초안을 만들고 `Display()`만 한다.
- Outlook COM 객체는 UI 스레드에서만 접근한다. 읽은 내용은 즉시 `MailSnapshot`으로 복사한다. LLM·RAG·공유 폴더 접근은 백그라운드(`Task.Run`)에서 실행한다.
- `ThisAddIn_Startup`에서는 무거운 작업을 하지 않는다. 설정·색인·ONNX는 첫 사용 시 지연 로딩한다.
- 애드인 이벤트 핸들러(리본 콜백·버튼·초안)는 모두 try/catch로 감싸 로그를 남기고 한국어 메시지를 표시한다.
- 로그 위치는 `%LOCALAPPDATA%\TechSupportReply\logs\yyyy-MM-dd.log`이다. 메일 본문과 API 키는 기록하지 않는다.
- API 키는 DPAPI(`secrets.dat`)에 저장하거나, 프로필의 `ApiKeyEnvVar`로 지정한 환경 변수에서 읽는다(프로세스 → 사용자 → 시스템 순서).
- 기본 모델(2026-09-28 실제 호출로 확인): Claude `claude-opus-5`, OpenAI `gpt-5.1`, xAI `grok-4`(Base URL `https://api.x.ai/v1`, OpenAI 호환).
- net48 함정: 실행 프로젝트가 `Microsoft.ML.OnnxRuntime`을 직접 참조하지 않으면 System32의 onnxruntime.dll(1.17)이 로드된다. 그래서 애드인은 패키지를 직접 참조하고, 애드인 폴더의 DLL을 전체 경로로 선로딩한다. `SQLitePCLRaw.bundle_e_sqlite3`는 2.1.13으로 고정한다.
- VSTO 빌드는 매니페스트 서명이 필수다(서명하지 않으면 "ClickOnce manifest signing option is not selected" 오류). 인증서 지문은 git에 넣지 않는 `Signing.user.props`에 둔다.
- VSTO 프로젝트는 `dotnet build`로 빌드할 수 없다. VS 2022 `MSBuild.exe`(`tools/build-addin.ps1`)를 쓴다. 테스트는 계속 `dotnet test tests/TechSupportReply.Tests`로 실행한다.
- 코드 스타일은 기존 코드와 같다. LangVersion 12, Nullable 끔, ImplicitUsings 끔, 한국어 XML 주석과 사용자 메시지를 쓴다.

## Review Focus

1. **공유 폴더(UNC)가 오프라인일 때** SMB 타임아웃(수십 초) 동안 Outlook UI가 멈추면 안 된다. `GetSession()`(공유 폴더 접근)은 백그라운드 스레드에서만 호출되어야 한다. → Task 7에서 테스트 `LoadMail_BuildsSessionOffUiThread_AndShowsMailImmediately`로 고정한다.
2. **답변 생성 중 다른 메일을 선택하면** 이전 메일의 스트리밍 조각이 새 메일의 미리보기에 섞이면 안 된다. 이전 생성은 취소되어야 한다. → Task 7 `LoadingAnotherMail_CancelsGeneration_AndIgnoresStaleOutput`.
3. **시스템 환경 변수를 Outlook이 실행된 뒤에 추가한 경우** 프로세스 환경에는 키가 없다. 사용자·시스템 레지스트리 값으로 대체해 읽어야 한다. → Task 1 `EnvironmentVariablesTests.FallsBackToUserThenMachine`.
4. **[답변 생성]을 연속으로 누르거나 생성 중에 다시 누르면** LLM이 중복 호출되면 안 된다. → Task 7 `Generate_WhileGenerating_IsIgnored`.
5. **HTML 회신 합성** 시 답변 속 `<`, `&`, 사용자가 편집한 줄바꿈이 안전하게 인코딩되어야 한다. Word가 만든 `<body lang=KO link=...>` 뒤에 삽입하고, 서명과 인용은 보존해야 한다. → Task 3 테스트들. 일반 텍스트 원문 메일은 Task 11의 `ReplyDraftWriter`에서 분기하고, Task 14 E2E 시나리오 3에서 확인한다.

---

## File Structure

```
src/TechSupportReply.Core/
  Settings/AppSettings.cs            (수정) LlmProfile.ApiKeyEnvVar, WorkspaceId, AppSettings.LastProfileId(Task 15)
  Settings/EnvironmentVariables.cs   (신규) 프로세스→사용자→시스템 환경 변수 조회
  Settings/ProfilePresets.cs         (신규) Claude/OpenAI/xAI 기본 프로필
  Settings/DefaultProfileSeeder.cs   (신규) 프로필이 없을 때 환경 변수로 기본 프로필 생성
  Llm/LlmException.cs                (수정) WorkspaceRequired, NotConfigured
  Llm/LlmProviderFactory.cs          (수정) 환경 변수 키, 주입 가능한 생성기
  Llm/AnthropicProvider.cs           (수정) anthropic-workspace-id 헤더, 워크스페이스 오류 변환, IsSupportedModel(Task 15)
  Llm/OpenAiProvider.cs              (수정, Task 15) Translate를 internal로(모델 목록 오류 변환 공유)
  Llm/LlmConnectionTester.cs         (신규) [연결 테스트]
  Llm/LlmModelLister.cs              (신규, Task 15) [모델 목록 불러오기]: Anthropic /v1/models, OpenAI 호환 /models
  Text/ReplyHtmlComposer.cs          (신규) 답변 텍스트 → HTML, 회신 본문 맨 앞 삽입
  Diagnostics/FileLog.cs             (신규) 날짜별 로그
src/TechSupportReply.App/            (신규, SDK 스타일 net48 WinForms)
  TechSupportReply.App.csproj
  Hosting/NativeLibraryPreloader.cs  onnxruntime.dll·e_sqlite3.dll 전체 경로 선로딩
  Hosting/RibbonMarkup.cs            리본 XML(Explorer / 읽기 창)
  Hosting/AddInPaths.cs              설정·캐시·로그·네이티브 폴더
  Hosting/KnowledgeSession.cs        카탈로그·검색기·지침 묶음
  Hosting/AddInServices.cs           조립 루트(IReplyBackend, ISettingsHost)
  Mail/AttachmentTextBuilder.cs      텍스트 첨부 선별·발췌
  Pane/IReplyPaneView.cs             작업창 뷰 계약 + PaneState
  Pane/IReplyBackend.cs              (Task 15: HasUsableKey, SaveLastProfile)
  Pane/ReplyPanePresenter.cs         분류→변경→생성(스트리밍)→초안 흐름, 키 있는 프로필만 표시·마지막 선택 기억(Task 15)
  Pane/ReplyTaskPaneControl.cs       WinForms UserControl(IReplyPaneView), 프로필 "표시명 · 모델"(Task 15)
  SettingsUi/ISettingsHost.cs        (Task 15: ListModelsAsync)
  SettingsUi/SettingsEditor.cs       프로필 CRUD·비밀·검증(순수 로직)
  SettingsUi/SettingsForm.cs         WinForms 설정 대화상자, [모델 목록 불러오기](Task 15)
src/TechSupportReply.AddIn/          (신규, VSTO 구형 csproj)
  TechSupportReply.AddIn.csproj
  Properties/AssemblyInfo.cs
  ThisAddIn.cs / ThisAddIn.Designer.cs / ThisAddIn.Designer.xml
  TechSupportRibbon.cs               IRibbonExtensibility 콜백
  Outlook/MailExtractor.cs           MailItem → MailSnapshot
  Outlook/ReplyDraftWriter.cs        ReplyAll 초안
  Outlook/TaskPaneManager.cs         창별 작업창
tools/
  New-DevSigningCert.ps1             코드 서명 인증서 + Signing.user.props
  build-addin.ps1                    MSBuild 빌드
  install-addin.ps1                  방법 B: 폴더 복사 + HKCU 등록 + 신뢰 목록(제거 포함)
  publish-addin.ps1                  방법 A: ClickOnce 게시
docs/설치가이드.md, docs/관리자가이드.md, docs/사용자가이드.md
tests/TechSupportReply.Tests/
  Core/Settings/EnvironmentVariablesTests.cs, DefaultProfileSeederTests.cs
  Core/Llm/LlmProviderFactoryTests.cs(수정), AnthropicWorkspaceTests.cs, LlmConnectionTesterTests.cs, LlmModelListerTests.cs(Task 15)
  Core/Settings/SettingsStoreTests.cs(수정, Task 15)
  Core/Text/ReplyHtmlComposerTests.cs
  Core/Diagnostics/FileLogTests.cs
  App/NativeLibraryPreloaderTests.cs, RibbonMarkupTests.cs, AttachmentTextBuilderTests.cs,
      AddInServicesTests.cs, ReplyPanePresenterTests.cs, ReplyTaskPaneControlTests.cs,
      SettingsEditorTests.cs, SettingsFormTests.cs
  TestSupport/Sta.cs, FakePaneView.cs, FakeBackend.cs, StubHttpHandler.cs(Task 15)
```

테스트 명령은 모두 저장소 루트에서 실행한다. 형식은 `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~<클래스명>"`이다.

---

### Task 1: 환경 변수 API 키 · Anthropic 워크스페이스 ID

**Files:**
- Modify: `src/TechSupportReply.Core/Settings/AppSettings.cs` (LlmProfile)
- Create: `src/TechSupportReply.Core/Settings/EnvironmentVariables.cs`
- Modify: `src/TechSupportReply.Core/Llm/LlmException.cs`
- Modify: `src/TechSupportReply.Core/Llm/LlmProviderFactory.cs`
- Modify: `src/TechSupportReply.Core/Llm/AnthropicProvider.cs` (생성자, `Translate`)
- Test: `tests/TechSupportReply.Tests/Core/Settings/EnvironmentVariablesTests.cs`, `tests/TechSupportReply.Tests/Core/Llm/LlmProviderFactoryTests.cs`, `tests/TechSupportReply.Tests/Core/Llm/AnthropicWorkspaceTests.cs`, `tests/TechSupportReply.Tests/Core/Llm/LlmIntegrationTests.cs`

**Interfaces:**
- Produces:
  - `LlmProfile.ApiKeyEnvVar : string` (기본 ""), `LlmProfile.WorkspaceId : string` (기본 "")
  - `static class EnvironmentVariables { static string Get(string name); internal static string Get(string name, Func<string, EnvironmentVariableTarget?, string> lookup); }`
  - `LlmErrorKind.WorkspaceRequired`, `LlmErrorKind.NotConfigured` (UserMessage = Message)
  - `LlmProviderFactory(SecretStore secrets, Func<string,string> getEnv = null, Func<LlmProfile,string,ILlmProvider> create = null)`, `string ResolveApiKey(LlmProfile)`, `ILlmProvider Create(LlmProfile)`(키 없으면 `NotConfigured`)
  - `internal AnthropicProvider(LlmProfile profile, string apiKey, HttpMessageHandler handler)`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/TechSupportReply.Tests/Core/Settings/EnvironmentVariablesTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using TechSupportReply.Core.Settings;
using Xunit;

namespace TechSupportReply.Tests.Core.Settings
{
    public class EnvironmentVariablesTests
    {
        private static Func<string, EnvironmentVariableTarget?, string> Lookup(string process, string user, string machine) =>
            (name, target) => target == null ? process : target == EnvironmentVariableTarget.User ? user : machine;

        [Fact]
        public void PrefersProcessValue()
        {
            Assert.Equal("p", EnvironmentVariables.Get("K", Lookup("p", "u", "m")));
        }

        [Fact]
        public void FallsBackToUserThenMachine()
        {
            Assert.Equal("u", EnvironmentVariables.Get("K", Lookup(null, "u", "m")));
            Assert.Equal("m", EnvironmentVariables.Get("K", Lookup("  ", null, "m")));
        }

        [Fact]
        public void TrimsValue_AndReturnsNullWhenMissing()
        {
            Assert.Equal("abc", EnvironmentVariables.Get("K", Lookup(" abc \r\n", null, null)));
            Assert.Null(EnvironmentVariables.Get("K", Lookup(null, null, null)));
            Assert.Null(EnvironmentVariables.Get(" ", Lookup("p", "u", "m")));
        }
    }
}
```

`tests/TechSupportReply.Tests/Core/Llm/LlmProviderFactoryTests.cs`에서 기존 `Create_MissingSecret_ThrowsAuthenticationWithProfileName`을 아래로 **교체**하고, 테스트를 추가한다:
```csharp
        [Fact]
        public void Create_MissingSecret_ThrowsNotConfiguredWithProfileName()
        {
            using (var tmp = new TempDir())
            {
                var ex = Assert.Throws<LlmException>(() => new LlmProviderFactory(new SecretStore(tmp.Root), _ => null).Create(
                    new LlmProfile { DisplayName = "개인 키", Provider = LlmProviderKind.OpenAI, Model = "m", SecretId = "none" }));
                Assert.Equal(LlmErrorKind.NotConfigured, ex.Kind);
                Assert.Contains("개인 키", ex.UserMessage);
            }
        }

        [Fact]
        public void ResolveApiKey_EnvVarWins_ThenFallsBackToSecret()
        {
            using (var tmp = new TempDir())
            {
                var secrets = new SecretStore(tmp.Root);
                secrets.Set("s1", "stored");
                var profile = new LlmProfile { Provider = LlmProviderKind.OpenAI, Model = "m", SecretId = "s1", ApiKeyEnvVar = "MY_KEY" };

                Assert.Equal("from-env", new LlmProviderFactory(secrets, n => n == "MY_KEY" ? "from-env" : null).ResolveApiKey(profile));
                Assert.Equal("stored", new LlmProviderFactory(secrets, _ => null).ResolveApiKey(profile));
            }
        }

        [Fact]
        public void Create_PassesResolvedKeyToInjectedFactory()
        {
            using (var tmp = new TempDir())
            {
                string seenKey = null;
                var fake = new FakeLlmProvider();
                var factory = new LlmProviderFactory(new SecretStore(tmp.Root), _ => "env-key", (p, k) => { seenKey = k; return fake; });
                Assert.Same(fake, factory.Create(new LlmProfile { DisplayName = "x", Provider = LlmProviderKind.OpenAI, Model = "m", ApiKeyEnvVar = "X" }));
                Assert.Equal("env-key", seenKey);
            }
        }

        [Fact]
        public void Create_EnvVarProfileWithoutValue_MentionsVariableName()
        {
            using (var tmp = new TempDir())
            {
                var ex = Assert.Throws<LlmException>(() => new LlmProviderFactory(new SecretStore(tmp.Root), _ => null).Create(
                    new LlmProfile { DisplayName = "Claude", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", ApiKeyEnvVar = "ANTHROPIC_API_KEY" }));
                Assert.Equal(LlmErrorKind.NotConfigured, ex.Kind);
                Assert.Contains("ANTHROPIC_API_KEY", ex.UserMessage);
            }
        }
```

`tests/TechSupportReply.Tests/Core/Llm/AnthropicWorkspaceTests.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class AnthropicWorkspaceTests
    {
        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;
            public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

            public CapturingHandler(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request);
                return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
            }
        }

        private static LlmRequest Hello()
        {
            var r = new LlmRequest { CachedSystem = "테스트", MaxTokens = 100 };
            r.Messages.Add(LlmMessage.User("hi"));
            return r;
        }

        private static AnthropicProvider Provider(string workspaceId, HttpMessageHandler handler) =>
            new AnthropicProvider(new LlmProfile { DisplayName = "t", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", WorkspaceId = workspaceId }, "sk-ant-test", handler);

        [Fact]
        public async Task WorkspaceId_IsSentAsHeader()
        {
            var handler = new CapturingHandler(HttpStatusCode.Unauthorized,
                "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}");
            var ex = await Assert.ThrowsAsync<LlmException>(() => Provider(" wrkspc_123 ", handler).CompleteAsync(Hello(), CancellationToken.None));
            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
            var request = handler.Requests.First();
            Assert.True(request.Headers.TryGetValues("anthropic-workspace-id", out var values));
            Assert.Equal("wrkspc_123", values.Single());
        }

        [Fact]
        public async Task NoWorkspaceId_HeaderAbsent()
        {
            var handler = new CapturingHandler(HttpStatusCode.Unauthorized,
                "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}");
            await Assert.ThrowsAsync<LlmException>(() => Provider("", handler).CompleteAsync(Hello(), CancellationToken.None));
            Assert.False(handler.Requests.First().Headers.Contains("anthropic-workspace-id"));
        }

        [Fact]
        public async Task WorkspaceRequiredError_IsTranslated()
        {
            var handler = new CapturingHandler(HttpStatusCode.BadRequest,
                "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"This API key is not scoped to a workspace, so this request must include the anthropic-workspace-id header with the ID of the workspace to use.\"}}");
            var ex = await Assert.ThrowsAsync<LlmException>(() => Provider("", handler).CompleteAsync(Hello(), CancellationToken.None));
            Assert.Equal(LlmErrorKind.WorkspaceRequired, ex.Kind);
            Assert.Contains("Workspace ID", ex.UserMessage);
        }
    }
}
```

`LlmIntegrationTests.cs`의 `Anthropic(string key)` 도우미에 워크스페이스 ID를 넣는다:
```csharp
        private static AnthropicProvider Anthropic(string key) => new AnthropicProvider(new LlmProfile
        {
            DisplayName = "it",
            Provider = LlmProviderKind.Anthropic,
            Model = Env("ANTHROPIC_TEST_MODEL") ?? "claude-opus-5",
            WorkspaceId = Env("ANTHROPIC_WORKSPACE_ID") ?? "",
        }, key);
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~EnvironmentVariablesTests|FullyQualifiedName~LlmProviderFactoryTests|FullyQualifiedName~AnthropicWorkspaceTests"`
Expected: 컴파일 오류(`EnvironmentVariables`, `ApiKeyEnvVar`, `WorkspaceId`, `NotConfigured` 없음)

- [ ] **Step 3: 구현**

`AppSettings.cs`의 `LlmProfile`에서 `SecretId` 뒤에 다음을 추가한다:
```csharp
        /// <summary>비어 있지 않으면 이 환경 변수의 값을 API 키로 먼저 사용한다(예: ANTHROPIC_API_KEY). 값이 없으면 SecretId로 대체한다.</summary>
        public string ApiKeyEnvVar { get; set; } = "";
        /// <summary>Anthropic 워크스페이스 ID(선택). 워크스페이스에 속하지 않은 키는 anthropic-workspace-id 헤더가 필요하다.</summary>
        public string WorkspaceId { get; set; } = "";
```

`src/TechSupportReply.Core/Settings/EnvironmentVariables.cs`:
```csharp
using System;
using System.Security;

namespace TechSupportReply.Core.Settings
{
    /// <summary>
    /// 환경 변수를 프로세스 → 사용자 → 시스템 순서로 읽는다. Outlook이 실행된 뒤 추가한 시스템 환경 변수는
    /// 프로세스 환경에 없으므로 레지스트리 값으로 대체한다.
    /// </summary>
    public static class EnvironmentVariables
    {
        private static readonly EnvironmentVariableTarget?[] Order =
            { null, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine };

        public static string Get(string name) =>
            Get(name, (n, target) => target == null ? Environment.GetEnvironmentVariable(n) : Environment.GetEnvironmentVariable(n, target.Value));

        internal static string Get(string name, Func<string, EnvironmentVariableTarget?, string> lookup)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var target in Order)
            {
                string value;
                try { value = lookup(name.Trim(), target); }
                catch (SecurityException) { value = null; }
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }
    }
}
```

`LlmException.cs`: enum의 `InvalidRequest,` 뒤에 `WorkspaceRequired,`와 `NotConfigured,`를 추가한다. `UserMessage` switch의 `InvalidRequest` 줄 앞에 다음을 추가한다:
```csharp
            LlmErrorKind.WorkspaceRequired => "이 API 키는 워크스페이스에 속해 있지 않아 Workspace ID가 필요합니다. [설정] → LLM 프로필에서 Workspace ID(wrkspc_로 시작)를 입력하거나 워크스페이스에 속한 키를 사용하세요.",
            LlmErrorKind.NotConfigured => Message,
```

`LlmProviderFactory.cs` 전체:
```csharp
using System;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Llm
{
    public sealed class LlmProviderFactory
    {
        private readonly SecretStore _secrets;
        private readonly Func<string, string> _getEnv;
        private readonly Func<LlmProfile, string, ILlmProvider> _create;

        public LlmProviderFactory(SecretStore secrets, Func<string, string> getEnv = null, Func<LlmProfile, string, ILlmProvider> create = null)
        {
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            _getEnv = getEnv ?? EnvironmentVariables.Get;
            _create = create ?? Create;
        }

        /// <summary>환경 변수(지정된 경우) → DPAPI 비밀 순서로 API 키를 찾는다. 없으면 null.</summary>
        public string ResolveApiKey(LlmProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (!string.IsNullOrWhiteSpace(profile.ApiKeyEnvVar))
            {
                var fromEnv = _getEnv(profile.ApiKeyEnvVar.Trim());
                if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
            }
            var stored = _secrets.Get(profile.SecretId);
            return string.IsNullOrWhiteSpace(stored) ? null : stored;
        }

        public ILlmProvider Create(LlmProfile profile)
        {
            var key = ResolveApiKey(profile);
            if (key == null)
            {
                var hint = string.IsNullOrWhiteSpace(profile.ApiKeyEnvVar)
                    ? "[설정]에서 API 키를 입력하세요."
                    : $"환경 변수 {profile.ApiKeyEnvVar.Trim()}에 값이 없습니다. 환경 변수를 설정한 뒤 Outlook을 다시 시작하거나 [설정]에서 키를 직접 입력하세요.";
                throw new LlmException(LlmErrorKind.NotConfigured, $"'{profile.DisplayName}' 프로필에 API 키가 없습니다. {hint}");
            }
            return _create(profile, key);
        }

        public static ILlmProvider Create(LlmProfile profile, string apiKey)
        {
            switch (profile.Provider)
            {
                case LlmProviderKind.Anthropic: return new AnthropicProvider(profile, apiKey);
                case LlmProviderKind.OpenAI: return new OpenAiProvider(profile, apiKey);
                default: throw new NotSupportedException($"지원하지 않는 공급자: {profile.Provider}");
            }
        }
    }
}
```

`AnthropicProvider.cs`의 생성자를 다음으로 교체한다(`using System.Net.Http;`는 이미 있음):
```csharp
        public AnthropicProvider(LlmProfile profile, string apiKey)
            : this(profile, apiKey, null)
        {
        }

        /// <summary>handler는 테스트용이다. null이고 WorkspaceId도 없으면 SDK 기본 HttpClient를 쓴다.</summary>
        internal AnthropicProvider(LlmProfile profile, string apiKey, HttpMessageHandler handler)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            EnsureSupportedModel(profile.Model);
            if (string.IsNullOrWhiteSpace(apiKey)) throw new LlmException(LlmErrorKind.Authentication, "API 키가 비어 있습니다.");
            var workspaceId = (profile.WorkspaceId ?? "").Trim();
            var handlers = new[] { new BetaRefusalFallbackHandler { Fallbacks = [new(Model.ClaudeOpus4_8)] } };
            _client = workspaceId.Length == 0 && handler == null
                ? new AnthropicClient { ApiKey = apiKey, Handlers = handlers }
                : new AnthropicClient { ApiKey = apiKey, Handlers = handlers, HttpClient = CreateHttpClient(workspaceId, handler) };
        }

        internal static HttpClient CreateHttpClient(string workspaceId, HttpMessageHandler handler)
        {
            var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            // 요청별 시간 제한은 SDK의 Timeout 설정이 맡는다. HttpClient 기본 100초는 긴 스트리밍을 끊는다.
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            if (!string.IsNullOrEmpty(workspaceId)) http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-workspace-id", workspaceId);
            return http;
        }
```
(`Handlers` 타입이 배열 대입과 맞지 않아 컴파일 오류가 나면, 기존처럼 컬렉션 식 `[new BetaRefusalFallbackHandler { ... }]`을 두 초기화 식에 각각 쓴다.)

`Translate`의 `switch (ex)` 바로 앞에 다음을 추가한다:
```csharp
            if (!(ex is LlmException) && !(ex is OperationCanceledException)
                && (ex.Message ?? "").IndexOf("anthropic-workspace-id", StringComparison.OrdinalIgnoreCase) >= 0)
                return new LlmException(LlmErrorKind.WorkspaceRequired, ex.Message, ex);
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~EnvironmentVariablesTests|FullyQualifiedName~LlmProviderFactoryTests|FullyQualifiedName~AnthropicWorkspaceTests|FullyQualifiedName~AnthropicProviderTests|FullyQualifiedName~LlmExceptionTests"`
Expected: 모두 PASS. 이어서 `dotnet test tests/TechSupportReply.Tests`로 전체 회귀가 없는지 확인한다(기존 207 통과/6 건너뜀 + 신규).

- [ ] **Step 5: 커밋**

```bash
git add src/TechSupportReply.Core tests/TechSupportReply.Tests
git commit -m "feat(core): 환경 변수 API 키와 Anthropic 워크스페이스 ID 헤더 지원"
```

---

### Task 2: 기본 프로필 프리셋·환경 변수 시드·연결 테스트

**Files:**
- Create: `src/TechSupportReply.Core/Settings/ProfilePresets.cs`, `src/TechSupportReply.Core/Settings/DefaultProfileSeeder.cs`, `src/TechSupportReply.Core/Llm/LlmConnectionTester.cs`
- Test: `tests/TechSupportReply.Tests/Core/Settings/DefaultProfileSeederTests.cs`, `tests/TechSupportReply.Tests/Core/Llm/LlmConnectionTesterTests.cs`

**Interfaces:**
- Consumes: Task 1의 `LlmProfile.ApiKeyEnvVar/WorkspaceId`
- Produces:
  - `enum ProfilePreset { Claude, OpenAI, Xai }`, `static class ProfilePresets { const string ClaudeModel="claude-opus-5", OpenAiModel="gpt-5.1", XaiModel="grok-4", XaiBaseUrl="https://api.x.ai/v1", AnthropicKeyEnv="ANTHROPIC_API_KEY", OpenAiKeyEnv="OPENAI_API_KEY", XaiKeyEnv="XAI_API_KEY", AnthropicWorkspaceEnv="ANTHROPIC_WORKSPACE_ID"; static LlmProfile Create(ProfilePreset); }`
  - `static class DefaultProfileSeeder { static bool SeedFromEnvironment(AppSettings settings, Func<string,string> getEnv); }`
  - `sealed class ConnectionTestResult { bool Success; string Message; }`, `static class LlmConnectionTester { static Task<ConnectionTestResult> TestAsync(ILlmProvider llm, CancellationToken ct); }`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/TechSupportReply.Tests/Core/Settings/DefaultProfileSeederTests.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using TechSupportReply.Core.Settings;
using Xunit;

namespace TechSupportReply.Tests.Core.Settings
{
    public class DefaultProfileSeederTests
    {
        private static System.Func<string, string> Env(Dictionary<string, string> d) => n => d.TryGetValue(n, out var v) ? v : null;

        [Fact]
        public void CreatesOneProfilePerAvailableKey_InClaudeOpenAiXaiOrder()
        {
            var s = new AppSettings();
            var added = DefaultProfileSeeder.SeedFromEnvironment(s, Env(new Dictionary<string, string>
            {
                ["ANTHROPIC_API_KEY"] = "a", ["OPENAI_API_KEY"] = "o", ["XAI_API_KEY"] = "x", ["ANTHROPIC_WORKSPACE_ID"] = "wrkspc_1",
            }));

            Assert.True(added);
            Assert.Equal(new[] { LlmProviderKind.Anthropic, LlmProviderKind.OpenAI, LlmProviderKind.OpenAI }, s.Profiles.Select(p => p.Provider));
            Assert.Equal(new[] { "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "XAI_API_KEY" }, s.Profiles.Select(p => p.ApiKeyEnvVar));
            Assert.Equal("wrkspc_1", s.Profiles[0].WorkspaceId);
            Assert.Equal("claude-opus-5", s.Profiles[0].Model);
            Assert.Equal("gpt-5.1", s.Profiles[1].Model);
            Assert.Equal("grok-4", s.Profiles[2].Model);
            Assert.Equal("https://api.x.ai/v1", s.Profiles[2].BaseUrl);
            Assert.Equal(s.Profiles[0].Id, s.DefaultProfileId);
            Assert.Equal(s.Profiles[0].Id, s.ClassifierProfileId);
            Assert.All(s.Profiles, p => Assert.Equal("", p.SecretId));
        }

        [Fact]
        public void SkipsMissingKeys()
        {
            var s = new AppSettings();
            DefaultProfileSeeder.SeedFromEnvironment(s, Env(new Dictionary<string, string> { ["XAI_API_KEY"] = "x" }));
            Assert.Single(s.Profiles);
            Assert.Equal(s.Profiles[0].Id, s.DefaultProfileId);
        }

        [Fact]
        public void DoesNothing_WhenProfilesExistOrNoKeys()
        {
            var existing = new AppSettings();
            existing.Profiles.Add(new LlmProfile { DisplayName = "mine" });
            Assert.False(DefaultProfileSeeder.SeedFromEnvironment(existing, _ => "k"));
            Assert.Single(existing.Profiles);

            var empty = new AppSettings();
            Assert.False(DefaultProfileSeeder.SeedFromEnvironment(empty, _ => null));
            Assert.Empty(empty.Profiles);
        }

        [Fact]
        public void Presets_HaveDistinctIds()
        {
            Assert.NotEqual(ProfilePresets.Create(ProfilePreset.Claude).Id, ProfilePresets.Create(ProfilePreset.Claude).Id);
            Assert.Equal("xAI Grok", ProfilePresets.Create(ProfilePreset.Xai).DisplayName);
        }
    }
}
```

`tests/TechSupportReply.Tests/Core/Llm/LlmConnectionTesterTests.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class LlmConnectionTesterTests
    {
        [Fact]
        public async Task Success_ReportsResponse()
        {
            var llm = new FakeLlmProvider().Enqueue("OK");
            var r = await LlmConnectionTester.TestAsync(llm, CancellationToken.None);
            Assert.True(r.Success);
            Assert.Contains("연결 성공", r.Message);
            Assert.Contains("OK", r.Message);
            Assert.Single(llm.Requests);
            Assert.Equal("low", llm.Requests[0].Effort);
        }

        [Fact]
        public async Task LlmException_UsesUserMessage()
        {
            var llm = new FakeLlmProvider { ThrowOnCall = new LlmException(LlmErrorKind.Authentication, "401") };
            var r = await LlmConnectionTester.TestAsync(llm, CancellationToken.None);
            Assert.False(r.Success);
            Assert.Equal(new LlmException(LlmErrorKind.Authentication, "401").UserMessage, r.Message);
        }

        [Fact]
        public async Task OtherException_ReportsMessage()
        {
            var llm = new FakeLlmProvider { ThrowOnCall = new InvalidOperationException("boom") };
            var r = await LlmConnectionTester.TestAsync(llm, CancellationToken.None);
            Assert.False(r.Success);
            Assert.Contains("boom", r.Message);
        }

        [Fact]
        public async Task Timeout_ReportsTimeout()
        {
            var llm = new FakeLlmProvider { Delay = TimeSpan.FromSeconds(10) };
            var r = await LlmConnectionTester.TestAsync(llm, CancellationToken.None, TimeSpan.FromMilliseconds(100));
            Assert.False(r.Success);
            Assert.Contains("시간", r.Message);
        }
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~DefaultProfileSeederTests|FullyQualifiedName~LlmConnectionTesterTests"`
Expected: 컴파일 오류(형식 없음)

- [ ] **Step 3: 구현**

`src/TechSupportReply.Core/Settings/ProfilePresets.cs`:
```csharp
namespace TechSupportReply.Core.Settings
{
    public enum ProfilePreset
    {
        Claude,
        OpenAI,
        Xai,
    }

    /// <summary>설정 화면의 [추가]와 환경 변수 시드가 함께 쓰는 기본 프로필. 모델명은 2026-09-28 실제 호출로 확인했다.</summary>
    public static class ProfilePresets
    {
        public const string ClaudeModel = "claude-opus-5";
        public const string OpenAiModel = "gpt-5.1";
        public const string XaiModel = "grok-4";
        public const string XaiBaseUrl = "https://api.x.ai/v1";
        public const string AnthropicKeyEnv = "ANTHROPIC_API_KEY";
        public const string OpenAiKeyEnv = "OPENAI_API_KEY";
        public const string XaiKeyEnv = "XAI_API_KEY";
        public const string AnthropicWorkspaceEnv = "ANTHROPIC_WORKSPACE_ID";

        public static LlmProfile Create(ProfilePreset preset)
        {
            switch (preset)
            {
                case ProfilePreset.Claude:
                    return new LlmProfile { DisplayName = "Claude", Provider = LlmProviderKind.Anthropic, Model = ClaudeModel, Effort = "medium" };
                case ProfilePreset.OpenAI:
                    return new LlmProfile { DisplayName = "OpenAI", Provider = LlmProviderKind.OpenAI, Model = OpenAiModel };
                default:
                    return new LlmProfile { DisplayName = "xAI Grok", Provider = LlmProviderKind.OpenAI, Model = XaiModel, BaseUrl = XaiBaseUrl };
            }
        }
    }
}
```

`src/TechSupportReply.Core/Settings/DefaultProfileSeeder.cs`:
```csharp
using System;

namespace TechSupportReply.Core.Settings
{
    /// <summary>처음 실행할 때(프로필 0개) 값이 있는 API 키 환경 변수마다 프로필을 만든다. 키 자체는 저장하지 않는다.</summary>
    public static class DefaultProfileSeeder
    {
        public static bool SeedFromEnvironment(AppSettings settings, Func<string, string> getEnv)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (getEnv == null) throw new ArgumentNullException(nameof(getEnv));
            if (settings.Profiles.Count > 0) return false;

            bool Has(string name) => !string.IsNullOrWhiteSpace(getEnv(name));

            if (Has(ProfilePresets.AnthropicKeyEnv))
            {
                var p = ProfilePresets.Create(ProfilePreset.Claude);
                p.ApiKeyEnvVar = ProfilePresets.AnthropicKeyEnv;
                p.WorkspaceId = (getEnv(ProfilePresets.AnthropicWorkspaceEnv) ?? "").Trim();
                settings.Profiles.Add(p);
            }
            if (Has(ProfilePresets.OpenAiKeyEnv))
            {
                var p = ProfilePresets.Create(ProfilePreset.OpenAI);
                p.ApiKeyEnvVar = ProfilePresets.OpenAiKeyEnv;
                settings.Profiles.Add(p);
            }
            if (Has(ProfilePresets.XaiKeyEnv))
            {
                var p = ProfilePresets.Create(ProfilePreset.Xai);
                p.ApiKeyEnvVar = ProfilePresets.XaiKeyEnv;
                settings.Profiles.Add(p);
            }
            if (settings.Profiles.Count == 0) return false;

            settings.DefaultProfileId = settings.Profiles[0].Id;
            settings.ClassifierProfileId = settings.Profiles[0].Id;
            return true;
        }
    }
}
```

`src/TechSupportReply.Core/Llm/LlmConnectionTester.cs`:
```csharp
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TechSupportReply.Core.Llm
{
    public sealed class ConnectionTestResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>설정 화면의 [연결 테스트]. 짧은 요청을 보내 키·모델·엔드포인트를 확인한다.</summary>
    public static class LlmConnectionTester
    {
        public static async Task<ConnectionTestResult> TestAsync(ILlmProvider llm, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (llm == null) throw new ArgumentNullException(nameof(llm));
            var request = new LlmRequest { CachedSystem = "연결 테스트입니다. 요청한 단어만 답하세요.", MaxTokens = 2000, Effort = "low" };
            request.Messages.Add(LlmMessage.User("OK라고만 답하세요."));
            var sw = Stopwatch.StartNew();
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
                try
                {
                    var text = (await llm.CompleteAsync(request, cts.Token).ConfigureAwait(false) ?? "").Trim();
                    if (text.Length > 40) text = text.Substring(0, 40) + "…";
                    return new ConnectionTestResult { Success = true, Message = $"연결 성공 ({sw.ElapsedMilliseconds} ms): {text}" };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new ConnectionTestResult { Message = "응답 시간이 초과되었습니다. 네트워크 또는 Base URL을 확인하세요." };
                }
                catch (LlmException ex)
                {
                    return new ConnectionTestResult { Message = ex.UserMessage };
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    return new ConnectionTestResult { Message = "연결 실패: " + ex.Message };
                }
            }
        }
    }
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~DefaultProfileSeederTests|FullyQualifiedName~LlmConnectionTesterTests"`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/TechSupportReply.Core tests/TechSupportReply.Tests
git commit -m "feat(core): Claude/OpenAI/xAI 기본 프로필, 환경 변수 시드, 연결 테스트"
```

---

### Task 3: 회신 HTML 합성기

**Files:**
- Create: `src/TechSupportReply.Core/Text/ReplyHtmlComposer.cs`
- Test: `tests/TechSupportReply.Tests/Core/Text/ReplyHtmlComposerTests.cs`

**Interfaces:**
- Produces: `static class ReplyHtmlComposer { const string BlockId = "tsr-reply"; static string ToHtml(string text); static string InsertAtTop(string existingHtml, string replyText); }`

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
using TechSupportReply.Core.Text;
using Xunit;

namespace TechSupportReply.Tests.Core.Text
{
    public class ReplyHtmlComposerTests
    {
        [Fact]
        public void ToHtml_EncodesSpecialCharacters()
        {
            var html = ReplyHtmlComposer.ToHtml("a < b & \"c\" <script>");
            Assert.Contains("a &lt; b &amp; &quot;c&quot; &lt;script&gt;", html);
            Assert.DoesNotContain("<script>", html);
        }

        [Fact]
        public void ToHtml_OneParagraphPerLine_BlankLinesKept()
        {
            var html = ReplyHtmlComposer.ToHtml("첫 줄\r\n\r\n셋째 줄\n");
            Assert.Contains(">첫 줄</p>", html);
            Assert.Contains(">&nbsp;</p>", html);
            Assert.Contains(">셋째 줄</p>", html);
            Assert.StartsWith("<div id=\"tsr-reply\"", html);
        }

        [Fact]
        public void ToHtml_PreservesLeadingSpaces()
        {
            Assert.Contains(">&nbsp;&nbsp;*CONTROL_TERMINATION</p>", ReplyHtmlComposer.ToHtml("  *CONTROL_TERMINATION"));
        }

        [Fact]
        public void InsertAtTop_AfterWordBodyTag_KeepsSignatureAndQuote()
        {
            var existing = "<html><head><style>p{}</style></head><body lang=KO link=\"#0563C1\" style='word-wrap:break-word'><div class=WordSection1><p>서명</p><div>-----Original Message-----</div></div></body></html>";
            var result = ReplyHtmlComposer.InsertAtTop(existing, "답변");
            var bodyEnd = result.IndexOf("style='word-wrap:break-word'>") + "style='word-wrap:break-word'>".Length;
            Assert.Equal(bodyEnd, result.IndexOf("<div id=\"tsr-reply\""));
            Assert.True(result.IndexOf(">답변</p>") < result.IndexOf("<p>서명</p>"));
            Assert.Contains("-----Original Message-----", result);
            Assert.EndsWith("</body></html>", result);
        }

        [Fact]
        public void InsertAtTop_BodyTagCaseInsensitive()
        {
            var result = ReplyHtmlComposer.InsertAtTop("<HTML><BODY>old</BODY></HTML>", "new");
            Assert.True(result.IndexOf(">new</p>") < result.IndexOf("old"));
        }

        [Fact]
        public void InsertAtTop_NoBody_Prepends()
        {
            Assert.StartsWith("<div id=\"tsr-reply\"", ReplyHtmlComposer.InsertAtTop("<p>old</p>", "new"));
        }

        [Fact]
        public void InsertAtTop_EmptyHtml_CreatesDocument()
        {
            var result = ReplyHtmlComposer.InsertAtTop("", "new");
            Assert.StartsWith("<html><body>", result);
            Assert.Contains(">new</p>", result);
        }
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~ReplyHtmlComposerTests"`
Expected: 컴파일 오류

- [ ] **Step 3: 구현**

```csharp
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TechSupportReply.Core.Text
{
    /// <summary>답변 텍스트를 Outlook HTML 문단으로 바꾸고, 회신 본문 맨 앞(body 태그 직후)에 넣는다. 서명·인용은 그대로 둔다.</summary>
    public static class ReplyHtmlComposer
    {
        public const string BlockId = "tsr-reply";
        private const string ParagraphOpen = "<p class=MsoNormal style=\"margin:0\">";
        private static readonly Regex BodyTag = new Regex(@"<body\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string ToHtml(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
            var sb = new StringBuilder();
            sb.Append("<div id=\"").Append(BlockId).Append("\" style=\"font-family:'맑은 고딕','Malgun Gothic',sans-serif;font-size:10pt\">");
            foreach (var line in lines)
            {
                sb.Append(ParagraphOpen);
                sb.Append(line.Trim().Length == 0 ? "&nbsp;" : EncodeLine(line));
                sb.Append("</p>");
            }
            sb.Append(ParagraphOpen).Append("&nbsp;</p></div>");
            return sb.ToString();
        }

        public static string InsertAtTop(string existingHtml, string replyText)
        {
            var block = ToHtml(replyText);
            if (string.IsNullOrWhiteSpace(existingHtml)) return "<html><body>" + block + "</body></html>";
            var m = BodyTag.Match(existingHtml);
            return m.Success ? existingHtml.Insert(m.Index + m.Length, block) : block + existingHtml;
        }

        private static string EncodeLine(string line)
        {
            int lead = 0;
            while (lead < line.Length && line[lead] == ' ') lead++;
            var sb = new StringBuilder();
            for (int i = 0; i < lead; i++) sb.Append("&nbsp;");
            sb.Append(WebUtility.HtmlEncode(line.Substring(lead)));
            return sb.ToString();
        }
    }
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~ReplyHtmlComposerTests"`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/TechSupportReply.Core/Text/ReplyHtmlComposer.cs tests/TechSupportReply.Tests/Core/Text/ReplyHtmlComposerTests.cs
git commit -m "feat(core): 회신 HTML 합성기(인코딩, body 직후 삽입, 서명·인용 보존)"
```

---

### Task 4: 날짜별 파일 로그

**Files:**
- Create: `src/TechSupportReply.Core/Diagnostics/FileLog.cs`
- Test: `tests/TechSupportReply.Tests/Core/Diagnostics/FileLogTests.cs`

**Interfaces:**
- Produces: `sealed class FileLog { FileLog(string directory, Func<DateTime> clock = null); static string DefaultDirectory; string CurrentPath; void Info(string); void Warn(string); void Error(string, Exception ex = null); void Cleanup(int keepDays = 30); }` — 모든 메서드는 예외를 던지지 않는다.

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
using System;
using System.IO;
using TechSupportReply.Core.Diagnostics;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Diagnostics
{
    public class FileLogTests
    {
        [Fact]
        public void WritesToDailyFile_WithLevelAndException()
        {
            using (var tmp = new TempDir())
            {
                var log = new FileLog(tmp.Root, () => new DateTime(2026, 9, 28, 13, 5, 7));
                log.Info("시작");
                log.Error("실패", new InvalidOperationException("boom"));

                var path = Path.Combine(tmp.Root, "2026-09-28.log");
                Assert.Equal(path, log.CurrentPath);
                var text = File.ReadAllText(path);
                Assert.Contains("2026-09-28 13:05:07.000 INFO 시작", text);
                Assert.Contains("ERROR 실패 | System.InvalidOperationException: boom", text);
            }
        }

        [Fact]
        public void NeverThrows_WhenDirectoryIsInvalid()
        {
            using (var tmp = new TempDir())
            {
                var fileInsteadOfDir = tmp.File("not-a-dir", "x");
                var log = new FileLog(fileInsteadOfDir);
                log.Info("무시되어야 함");
                log.Cleanup();
            }
        }

        [Fact]
        public void Cleanup_DeletesOnlyOldLogFiles()
        {
            using (var tmp = new TempDir())
            {
                var old = tmp.File("2026-08-01.log", "x");
                var recent = tmp.File("2026-09-20.log", "x");
                var other = tmp.File("notes.log", "x");
                new FileLog(tmp.Root, () => new DateTime(2026, 9, 28)).Cleanup(keepDays: 30);
                Assert.False(File.Exists(old));
                Assert.True(File.Exists(recent));
                Assert.True(File.Exists(other));
            }
        }
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~FileLogTests"`
Expected: 컴파일 오류

- [ ] **Step 3: 구현**

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TechSupportReply.Core.Diagnostics
{
    /// <summary>
    /// %LOCALAPPDATA%\TechSupportReply\logs\yyyy-MM-dd.log에 한 줄씩 기록한다. 메일 본문과 API 키는 넣지 않는다.
    /// 로깅 실패가 Outlook 동작을 막으면 안 되므로 어떤 메서드도 예외를 던지지 않는다.
    /// </summary>
    public sealed class FileLog
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly string _dir;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();

        public FileLog(string directory, Func<DateTime> clock = null)
        {
            _dir = directory ?? DefaultDirectory;
            _clock = clock ?? (() => DateTime.Now);
        }

        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TechSupportReply", "logs");

        public string CurrentPath => PathFor(_clock());

        public void Info(string message) => Write("INFO", message, null);

        public void Warn(string message) => Write("WARN", message, null);

        public void Error(string message, Exception ex = null) => Write("ERROR", message, ex);

        public void Cleanup(int keepDays = 30)
        {
            try
            {
                if (!Directory.Exists(_dir)) return;
                var cutoff = _clock().Date.AddDays(-keepDays);
                foreach (var file in Directory.GetFiles(_dir, "????-??-??.log"))
                {
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                        && day < cutoff)
                        File.Delete(file);
                }
            }
            catch (Exception)
            {
                // 정리 실패는 무시한다.
            }
        }

        private string PathFor(DateTime time) =>
            Path.Combine(_dir, time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        private void Write(string level, string message, Exception ex)
        {
            try
            {
                var now = _clock();
                var line = new StringBuilder()
                    .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(level).Append(' ').Append(message ?? "");
                if (ex != null) line.Append(" | ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message).Append('\n').Append(ex.StackTrace);
                line.Append('\n');
                lock (_lock)
                {
                    Directory.CreateDirectory(_dir);
                    File.AppendAllText(PathFor(now), line.ToString(), Utf8);
                }
            }
            catch (Exception)
            {
                // 로그를 쓸 수 없어도 계속 진행한다.
            }
        }
    }
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~FileLogTests"`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/TechSupportReply.Core/Diagnostics tests/TechSupportReply.Tests/Core/Diagnostics
git commit -m "feat(core): 예외를 던지지 않는 날짜별 파일 로그"
```

---

### Task 5: App 프로젝트 · 네이티브 DLL 선로딩 · 리본 XML · 첨부 텍스트

**Files:**
- Create: `src/TechSupportReply.App/TechSupportReply.App.csproj`
- Create: `src/TechSupportReply.App/Hosting/NativeLibraryPreloader.cs`, `src/TechSupportReply.App/Hosting/RibbonMarkup.cs`, `src/TechSupportReply.App/Mail/AttachmentTextBuilder.cs`
- Modify: `tests/TechSupportReply.Tests/TechSupportReply.Tests.csproj` (App 참조), `TechSupportReply.sln` (App 추가)
- Create: `tests/TechSupportReply.Tests/TestSupport/Sta.cs`
- Test: `tests/TechSupportReply.Tests/App/NativeLibraryPreloaderTests.cs`, `RibbonMarkupTests.cs`, `AttachmentTextBuilderTests.cs`

**Interfaces:**
- Produces:
  - `static class NativeLibraryPreloader { static readonly string[] Libraries; static IReadOnlyList<string> Preload(string directory); static string LoadedModulePath(string moduleName); static string AssemblyDirectory(Assembly assembly); }` (반환값은 경고 목록)
  - `static class RibbonMarkup { const string ExplorerRibbonId="Microsoft.Outlook.Explorer", ReadMailRibbonId="Microsoft.Outlook.Mail.Read"; static string Get(string ribbonId); }` — 콜백 이름 `OnRibbonLoad`, `OnReplyClick`, `OnSettingsClick`
  - `sealed class SavedAttachment { SavedAttachment(string fileName, string path); string FileName; string Path; }`
  - `static class AttachmentTextBuilder { const long MaxFileBytes; const int MaxTotalChars=20000, HeadChars=3000, TailChars=5000; static bool IsInlineImage(string name); static bool IsTextCandidate(string name, long size); static string SafeFileName(string name); static string Build(IEnumerable<SavedAttachment> files, int maxTotalChars = MaxTotalChars); }`
  - 테스트 지원: `static class Sta { static void Run(Action action); }`

- [ ] **Step 1: 프로젝트 골격 생성**

`src/TechSupportReply.App/TechSupportReply.App.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <UseWindowsForms>true</UseWindowsForms>
    <RootNamespace>TechSupportReply.App</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="TechSupportReply.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\TechSupportReply.Core\TechSupportReply.Core.csproj" />
    <ProjectReference Include="..\TechSupportReply.Rag\TechSupportReply.Rag.csproj" />
  </ItemGroup>
</Project>
```
Run: `dotnet sln TechSupportReply.sln add src/TechSupportReply.App/TechSupportReply.App.csproj --solution-folder src`
테스트 csproj의 ProjectReference 목록에 `<ProjectReference Include="..\..\src\TechSupportReply.App\TechSupportReply.App.csproj" />`를 추가한다.

`tests/TechSupportReply.Tests/TestSupport/Sta.cs`:
```csharp
using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace TechSupportReply.Tests.TestSupport
{
    /// <summary>WinForms 컨트롤 테스트를 STA 스레드에서 실행한다.</summary>
    internal static class Sta
    {
        public static void Run(Action action)
        {
            Exception error = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
```

- [ ] **Step 2: 실패하는 테스트 작성**

`tests/TechSupportReply.Tests/App/NativeLibraryPreloaderTests.cs`:
```csharp
using System;
using System.IO;
using TechSupportReply.App.Hosting;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class NativeLibraryPreloaderTests
    {
        [Fact]
        public void Preload_FromTestOutput_LoadsOnnxRuntimeFromThatFolder()
        {
            var dir = AppContext.BaseDirectory;
            var warnings = NativeLibraryPreloader.Preload(dir);
            Assert.Empty(warnings);
            var loaded = NativeLibraryPreloader.LoadedModulePath("onnxruntime.dll");
            Assert.StartsWith(Path.GetFullPath(dir).TrimEnd('\\'), loaded, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Preload_MissingFolder_ReturnsWarningPerLibrary()
        {
            using (var tmp = new TempDir())
            {
                var warnings = NativeLibraryPreloader.Preload(tmp.Root);
                Assert.Equal(NativeLibraryPreloader.Libraries.Length, warnings.Count);
                Assert.Contains(warnings, w => w.Contains("onnxruntime.dll"));
            }
        }

        [Fact]
        public void AssemblyDirectory_IsTestOutput()
        {
            Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'),
                NativeLibraryPreloader.AssemblyDirectory(typeof(NativeLibraryPreloader).Assembly).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);
        }
    }
}
```

`tests/TechSupportReply.Tests/App/RibbonMarkupTests.cs`:
```csharp
using System.Linq;
using System.Xml.Linq;
using TechSupportReply.App.Hosting;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class RibbonMarkupTests
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/office/2009/07/customui";

        [Theory]
        [InlineData(RibbonMarkup.ExplorerRibbonId, "TabMail")]
        [InlineData(RibbonMarkup.ReadMailRibbonId, "TabReadMessage")]
        public void Get_ReturnsWellFormedXml_ForSupportedRibbons(string ribbonId, string tab)
        {
            var doc = XDocument.Parse(RibbonMarkup.Get(ribbonId));
            Assert.Equal("OnRibbonLoad", (string)doc.Root.Attribute("onLoad"));
            Assert.Equal(tab, (string)doc.Descendants(Ns + "tab").Single().Attribute("idMso"));
            var actions = doc.Descendants(Ns + "button").Select(b => (string)b.Attribute("onAction")).ToList();
            Assert.Equal(new[] { "OnReplyClick", "OnSettingsClick" }, actions);
            var ids = doc.Descendants().Select(e => (string)e.Attribute("id")).Where(i => i != null).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }

        [Theory]
        [InlineData("Microsoft.Outlook.Mail.Compose")]
        [InlineData("")]
        [InlineData(null)]
        public void Get_ReturnsNull_ForOtherRibbons(string ribbonId)
        {
            Assert.Null(RibbonMarkup.Get(ribbonId));
        }
    }
}
```

`tests/TechSupportReply.Tests/App/AttachmentTextBuilderTests.cs`:
```csharp
using System.IO;
using System.Linq;
using System.Text;
using TechSupportReply.App.Mail;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class AttachmentTextBuilderTests
    {
        [Theory]
        [InlineData("d3hsp", 1000, true)]
        [InlineData("messag", 1000, true)]
        [InlineData("mes0003", 1000, true)]
        [InlineData("model.k", 1000, true)]
        [InlineData("run.LOG", 1000, true)]
        [InlineData("fluent.trn", 1000, true)]
        [InlineData("issues.csv", 1000, true)]
        [InlineData("manual.pdf", 1000, false)]
        [InlineData("image001.png", 1000, false)]
        [InlineData("huge.log", 21L * 1024 * 1024, false)]
        public void IsTextCandidate(string name, long size, bool expected)
        {
            Assert.Equal(expected, AttachmentTextBuilder.IsTextCandidate(name, size));
        }

        [Theory]
        [InlineData("image001.png", true)]
        [InlineData("IMAGE012.JPG", true)]
        [InlineData("result.png", false)]
        public void IsInlineImage(string name, bool expected)
        {
            Assert.Equal(expected, AttachmentTextBuilder.IsInlineImage(name));
        }

        [Fact]
        public void SafeFileName_ReplacesInvalidChars()
        {
            Assert.Equal("a_b_c.txt", AttachmentTextBuilder.SafeFileName("a/b:c.txt"));
            Assert.Equal("attachment", AttachmentTextBuilder.SafeFileName(""));
        }

        [Fact]
        public void Build_LongFile_KeepsHeadAndTail()
        {
            using (var tmp = new TempDir())
            {
                var content = "HEAD" + new string('x', 20000) + "TAIL";
                var path = tmp.File("d3hsp", content);
                var text = AttachmentTextBuilder.Build(new[] { new SavedAttachment("d3hsp", path) });
                Assert.StartsWith("### d3hsp\nHEAD", text);
                Assert.EndsWith("TAIL", text);
                Assert.Contains("…(중략)…", text);
                Assert.True(text.Length < AttachmentTextBuilder.HeadChars + AttachmentTextBuilder.TailChars + 100);
            }
        }

        [Fact]
        public void Build_RespectsTotalLimit_AndSkipsMissingFiles()
        {
            using (var tmp = new TempDir())
            {
                var files = Enumerable.Range(0, 5).Select(i => new SavedAttachment($"f{i}.log", tmp.File($"f{i}.log", new string((char)('a' + i), 7000)))).ToList();
                files.Insert(0, new SavedAttachment("gone.log", Path.Combine(tmp.Root, "gone.log")));
                var text = AttachmentTextBuilder.Build(files, maxTotalChars: 15000);
                Assert.True(text.Length <= 15000 + 50);
                Assert.Contains("### f0.log", text);
                Assert.DoesNotContain("gone.log", text);
                Assert.Contains("생략", text);
            }
        }

        [Fact]
        public void Build_ReadsCp949()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Root, "korean.txt");
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                File.WriteAllText(path, "해석이 발산합니다", Encoding.GetEncoding(949));
                Assert.Contains("해석이 발산합니다", AttachmentTextBuilder.Build(new[] { new SavedAttachment("korean.txt", path) }));
            }
        }
    }
}
```

- [ ] **Step 3: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~TechSupportReply.Tests.App"`
Expected: 컴파일 오류(형식 없음)

- [ ] **Step 4: 구현**

`src/TechSupportReply.App/Hosting/NativeLibraryPreloader.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TechSupportReply.App.Hosting
{
    /// <summary>
    /// Outlook 프로세스에서 System32의 onnxruntime.dll(Windows 내장 구버전)이 먼저 로드되지 않도록
    /// 애드인 폴더의 네이티브 DLL을 전체 경로로 먼저 로드한다. 같은 모듈 이름이 이미 로드되어 있으면 이후 DllImport는 그것을 쓴다.
    /// </summary>
    public static class NativeLibraryPreloader
    {
        public static readonly string[] Libraries = { "onnxruntime.dll", "e_sqlite3.dll" };
        private const uint LoadWithAlteredSearchPath = 0x00000008;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileName(IntPtr module, StringBuilder fileName, uint size);

        public static IReadOnlyList<string> Preload(string directory)
        {
            var warnings = new List<string>();
            foreach (var lib in Libraries)
            {
                var path = Find(directory, lib);
                if (path == null)
                {
                    warnings.Add($"{lib}을(를) 찾을 수 없습니다: {directory}");
                    continue;
                }
                if (LoadLibraryEx(path, IntPtr.Zero, LoadWithAlteredSearchPath) == IntPtr.Zero)
                    warnings.Add($"{lib} 로드 실패(Win32 오류 {Marshal.GetLastWin32Error()}): {path}");
            }
            return warnings;
        }

        /// <summary>현재 프로세스에 로드된 모듈의 전체 경로. 로드되지 않았으면 null.</summary>
        public static string LoadedModulePath(string moduleName)
        {
            var handle = GetModuleHandle(moduleName);
            if (handle == IntPtr.Zero) return null;
            var sb = new StringBuilder(1024);
            return GetModuleFileName(handle, sb, (uint)sb.Capacity) == 0 ? null : sb.ToString();
        }

        /// <summary>섀도 복사와 무관하게 어셈블리 원본 폴더를 돌려준다(VSTO는 CodeBase 폴더에 네이티브 DLL이 있다).</summary>
        public static string AssemblyDirectory(Assembly assembly) =>
            Path.GetDirectoryName(new Uri(assembly.CodeBase).LocalPath);

        private static string Find(string directory, string lib)
        {
            if (string.IsNullOrEmpty(directory)) return null;
            foreach (var candidate in new[] { Path.Combine(directory, lib), Path.Combine(directory, "runtimes", "win-x64", "native", lib) })
                if (File.Exists(candidate)) return candidate;
            return null;
        }
    }
}
```

`src/TechSupportReply.App/Hosting/RibbonMarkup.cs`:
```csharp
namespace TechSupportReply.App.Hosting
{
    /// <summary>Outlook 리본 XML. 메일 목록(Explorer)의 [홈] 탭과 읽기 창의 [메시지] 탭에 같은 그룹을 추가한다.</summary>
    public static class RibbonMarkup
    {
        public const string ExplorerRibbonId = "Microsoft.Outlook.Explorer";
        public const string ReadMailRibbonId = "Microsoft.Outlook.Mail.Read";

        public static string Get(string ribbonId)
        {
            string tab, suffix;
            if (ribbonId == ExplorerRibbonId) { tab = "TabMail"; suffix = "Explorer"; }
            else if (ribbonId == ReadMailRibbonId) { tab = "TabReadMessage"; suffix = "Read"; }
            else return null;

            return "<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\" onLoad=\"OnRibbonLoad\">"
                 + "<ribbon><tabs><tab idMso=\"" + tab + "\">"
                 + "<group id=\"TsrGroup" + suffix + "\" label=\"기술지원\">"
                 + "<button id=\"TsrReply" + suffix + "\" label=\"기술지원 답변\" size=\"large\" imageMso=\"ReplyAll\""
                 + " screentip=\"기술지원 답변 초안\" supertip=\"선택한 메일의 제품군을 판별하고 지식 폴더를 검색해 답변을 생성합니다. 메일은 자동으로 발송하지 않습니다.\""
                 + " onAction=\"OnReplyClick\" />"
                 + "<button id=\"TsrSettings" + suffix + "\" label=\"설정\" size=\"normal\" imageMso=\"PropertySheet\" onAction=\"OnSettingsClick\" />"
                 + "</group></tab></tabs></ribbon></customUI>";
        }
    }
}
```

`src/TechSupportReply.App/Mail/AttachmentTextBuilder.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TechSupportReply.Rag.Loaders;

namespace TechSupportReply.App.Mail
{
    public sealed class SavedAttachment
    {
        public SavedAttachment(string fileName, string path)
        {
            FileName = fileName ?? "";
            Path = path ?? "";
        }

        public string FileName { get; }
        public string Path { get; }
    }

    /// <summary>
    /// 메일 첨부 중 로그·키워드 파일 같은 텍스트를 골라 프롬프트용 발췌를 만든다.
    /// 긴 파일(d3hsp 등)은 앞부분과 오류가 모이는 뒷부분만 남긴다.
    /// </summary>
    public static class AttachmentTextBuilder
    {
        public const long MaxFileBytes = 20L * 1024 * 1024;
        public const int MaxTotalChars = 20000;
        public const int HeadChars = 3000;
        public const int TailChars = 5000;

        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".csv", ".md", ".k", ".key", ".dyn", ".inp", ".out", ".trn", ".err", ".json", ".xml",
        };
        private static readonly Regex KnownNames = new Regex(@"^(d3hsp|messag|mes\d{4}|glstat)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InlineImage = new Regex(@"^image\d{3}\.(png|jpe?g|gif|bmp)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsInlineImage(string name) => InlineImage.IsMatch(name ?? "");

        public static bool IsTextCandidate(string name, long size)
        {
            if (string.IsNullOrWhiteSpace(name) || size > MaxFileBytes) return false;
            return TextExtensions.Contains(System.IO.Path.GetExtension(name)) || KnownNames.IsMatch(name);
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "attachment";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        public static string Build(IEnumerable<SavedAttachment> files, int maxTotalChars = MaxTotalChars)
        {
            var sb = new StringBuilder();
            foreach (var f in files ?? Enumerable.Empty<SavedAttachment>())
            {
                string text;
                try { text = TextFileReader.ReadAllText(f.Path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }

                var block = "### " + f.FileName + "\n" + Excerpt(text.Replace("\r\n", "\n")).Trim() + "\n\n";
                if (sb.Length + block.Length > maxTotalChars)
                {
                    var remain = maxTotalChars - sb.Length;
                    if (remain > 200) sb.Append(block.Substring(0, remain));
                    sb.Append("\n…(첨부 텍스트 한도 초과로 생략)…");
                    break;
                }
                sb.Append(block);
            }
            return sb.ToString().TrimEnd();
        }

        internal static string Excerpt(string text) =>
            text.Length <= HeadChars + TailChars
                ? text
                : text.Substring(0, HeadChars) + "\n…(중략)…\n" + text.Substring(text.Length - TailChars);
    }
}
```
(`TextFileReader.ReadAllText`는 Rag의 public API이며 CP949를 판별한다. `Build_ReadsCp949`가 실패하면 `src/TechSupportReply.Rag/Loaders/TextFileReader.cs`의 시그니처를 확인해 맞춘다.)

- [ ] **Step 5: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~TechSupportReply.Tests.App"`
Expected: PASS

- [ ] **Step 6: 커밋**

```bash
git add src/TechSupportReply.App tests/TechSupportReply.Tests TechSupportReply.sln
git commit -m "feat(app): App 프로젝트, 네이티브 DLL 선로딩, 리본 XML, 첨부 텍스트 발췌"
```

---

### Task 6: 조립 루트 AddInServices · KnowledgeSession

**Files:**
- Create: `src/TechSupportReply.App/Hosting/AddInPaths.cs`, `KnowledgeSession.cs`, `AddInServices.cs`
- Create: `src/TechSupportReply.App/Pane/IReplyBackend.cs`, `src/TechSupportReply.App/SettingsUi/ISettingsHost.cs`
- Test: `tests/TechSupportReply.Tests/App/AddInServicesTests.cs`

**Interfaces:**
- Consumes: Task 1~5(`EnvironmentVariables.Get`, `LlmProviderFactory(secrets, getEnv, create)`, `DefaultProfileSeeder`, `FileLog`, `NativeLibraryPreloader`), Plan A(`SettingsStore`, `SecretStore`, `IndexCacheSync`, `KnowledgeRetriever`, `ProductCatalog`, `KbLayout`, `OnnxEmbedder`, `ReplyGenerator`)
- Produces:
  - `sealed class AddInPaths { string SettingsDirectory, CacheDirectory, LogDirectory, NativeDirectory; static AddInPaths Default(); }`
  - `sealed class KnowledgeSession : IDisposable { KnowledgeSession(ProductCatalog catalog, IKnowledgeRetriever retriever, Func<ProductDefinition,string> guideLoader, IEnumerable<string> warnings, IDisposable owned = null); ProductCatalog Catalog; IKnowledgeRetriever Retriever; IndexCacheSync Sync {get; internal set;}; IReadOnlyList<string> Warnings; string LoadGuide(ProductDefinition); ReplyGenerator CreateGenerator(AppSettings); }`
  - `interface IReplyBackend { AppSettings Settings {get;} FileLog Log {get;} KnowledgeSession GetSession(); ILlmProvider CreateLlm(string profileId); }`
  - `interface ISettingsHost { AppSettings Settings {get;} SecretStore Secrets {get;} Func<string,string> GetEnv {get;} void ApplySettings(AppSettings settings); Task<string> SyncNowAsync(CancellationToken ct); IndexManifest LoadLocalManifest(); ILlmProvider CreateLlmForTest(LlmProfile profile, string apiKey); }`
  - `sealed class AddInServices : IReplyBackend, ISettingsHost, IDisposable { AddInServices(AddInPaths paths, Func<string,string> getEnv = null, Func<string,IEmbedder> embedderFactory = null, Func<LlmProfile,string,ILlmProvider> llmFactory = null); SettingsStore SettingsStore; ... }`

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.App.Hosting;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class AddInServicesTests
    {
        private static AddInPaths Paths(TempDir tmp) => new AddInPaths
        {
            SettingsDirectory = tmp.Sub("settings"),
            CacheDirectory = tmp.Sub("cache"),
            LogDirectory = tmp.Sub("logs"),
            NativeDirectory = null,
        };

        [Fact]
        public void FirstRun_SeedsProfilesFromEnvironment_AndSaves()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), n => n == "OPENAI_API_KEY" ? "sk" : null);
                Assert.Single(services.Settings.Profiles);
                Assert.Equal("OPENAI_API_KEY", services.Settings.Profiles[0].ApiKeyEnvVar);
                Assert.Single(new SettingsStore(tmp.Root + "\\settings").Load().Profiles);
            }
        }

        [Fact]
        public void NoRagRoot_SessionUsesDefaultCatalogWithoutRetriever_AndWarns()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                var session = services.GetSession();
                Assert.Null(session.Retriever);
                Assert.Equal(ProductCatalog.CreateDefault().Products.Count, session.Catalog.Products.Count);
                Assert.Contains(session.Warnings, w => w.Contains("RAG 루트"));
                Assert.Same(session, services.GetSession());
            }
        }

        [Fact]
        public void RagRoot_LoadsSharedProductsJson_AndCreatesRetriever()
        {
            using (var tmp = new TempDir())
            {
                var kb = tmp.Sub("kb");
                new ProductCatalog(new[]
                {
                    new ProductDefinition { Id = ProductCatalog.CommonId, DisplayName = "공통", Folder = "_common" },
                    new ProductDefinition { Id = "only-one", DisplayName = "하나", Folder = "One" },
                }).Save(KbLayout.ProductsPath(kb));
                var services = new AddInServices(Paths(tmp), _ => null, embedderFactory: _ => new FakeEmbedder());
                var s = services.Settings;
                s.RagRoot = kb;
                services.ApplySettings(s);

                var session = services.GetSession();
                Assert.NotNull(session.Retriever);
                Assert.NotNull(session.Sync);
                Assert.NotNull(session.Catalog.Find("only-one"));
            }
        }

        [Fact]
        public void ApplySettings_SavesAndRebuildsSession()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                var first = services.GetSession();
                var s = services.Settings;
                s.User.Name = "홍길동";
                services.ApplySettings(s);
                Assert.NotSame(first, services.GetSession());
                Assert.Equal("홍길동", new SettingsStore(Path.Combine(tmp.Root, "settings")).Load().User.Name);
            }
        }

        [Fact]
        public void CreateLlm_UsesEnvKey_AndFallsBackToDefaultProfile()
        {
            using (var tmp = new TempDir())
            {
                string seenKey = null;
                LlmProfile seenProfile = null;
                var services = new AddInServices(Paths(tmp), n => n == "XAI_API_KEY" ? "xai-key" : null,
                    llmFactory: (p, k) => { seenProfile = p; seenKey = k; return new FakeLlmProvider(); });
                services.CreateLlm("unknown-id");
                Assert.Equal("xai-key", seenKey);
                Assert.Equal("grok-4", seenProfile.Model);
            }
        }

        [Fact]
        public void CreateLlm_NoProfiles_ThrowsNotConfigured()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                var ex = Assert.Throws<LlmException>(() => services.CreateLlm(null));
                Assert.Equal(LlmErrorKind.NotConfigured, ex.Kind);
                Assert.Contains("설정", ex.UserMessage);
            }
        }

        [Fact]
        public async Task SyncNow_WithoutRagRoot_ReportsNotConfigured()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                Assert.Contains("설정되지", await services.SyncNowAsync(CancellationToken.None));
                Assert.Null(services.LoadLocalManifest());
            }
        }
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~AddInServicesTests"`
Expected: 컴파일 오류

- [ ] **Step 3: 구현**

`src/TechSupportReply.App/Hosting/AddInPaths.cs`:
```csharp
using TechSupportReply.Core.Diagnostics;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Sync;

namespace TechSupportReply.App.Hosting
{
    public sealed class AddInPaths
    {
        public string SettingsDirectory { get; set; } = SettingsStore.DefaultDirectory;
        public string CacheDirectory { get; set; } = IndexCacheSync.DefaultCacheDir;
        public string LogDirectory { get; set; } = FileLog.DefaultDirectory;
        /// <summary>onnxruntime.dll·e_sqlite3.dll이 있는 폴더. null이면 선로딩하지 않는다(테스트).</summary>
        public string NativeDirectory { get; set; }

        public static AddInPaths Default() => new AddInPaths
        {
            NativeDirectory = NativeLibraryPreloader.AssemblyDirectory(typeof(AddInPaths).Assembly),
        };
    }
}
```

`src/TechSupportReply.App/Hosting/KnowledgeSession.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using TechSupportReply.Core.Generation;
using TechSupportReply.Core.Knowledge;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Sync;

namespace TechSupportReply.App.Hosting
{
    /// <summary>현재 설정(RAG 루트)으로 만든 제품 목록·검색기·제품 지침 묶음. 설정이 바뀌면 새로 만든다.</summary>
    public sealed class KnowledgeSession : IDisposable
    {
        private readonly Func<ProductDefinition, string> _guideLoader;
        private readonly IDisposable _owned;

        public KnowledgeSession(ProductCatalog catalog, IKnowledgeRetriever retriever, Func<ProductDefinition, string> guideLoader,
            IEnumerable<string> warnings, IDisposable owned = null)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            Retriever = retriever;
            _guideLoader = guideLoader ?? (p => "");
            Warnings = (warnings ?? Enumerable.Empty<string>()).ToList();
            _owned = owned;
        }

        public ProductCatalog Catalog { get; }
        public IKnowledgeRetriever Retriever { get; }
        public IndexCacheSync Sync { get; internal set; }
        public IReadOnlyList<string> Warnings { get; }

        public string LoadGuide(ProductDefinition product) => _guideLoader(product);

        public ReplyGenerator CreateGenerator(AppSettings settings) => new ReplyGenerator(Catalog, Retriever, _guideLoader, settings);

        public void Dispose() => _owned?.Dispose();
    }
}
```

`src/TechSupportReply.App/Pane/IReplyBackend.cs`:
```csharp
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
```

`src/TechSupportReply.App/SettingsUi/ISettingsHost.cs`:
```csharp
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
```

`src/TechSupportReply.App/Hosting/AddInServices.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.App.Pane;
using TechSupportReply.App.SettingsUi;
using TechSupportReply.Core.Diagnostics;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Sync;

namespace TechSupportReply.App.Hosting
{
    /// <summary>
    /// 애드인의 조립 루트. 설정·비밀·로그를 소유하고, 지식 세션(카탈로그·검색기)을 첫 사용 시 만든다.
    /// 설정이 바뀌면 세션을 버리고 다음 사용 때 다시 만든다. 사용 중일 수 있는 이전 세션은 종료 시 정리한다.
    /// </summary>
    public sealed class AddInServices : IReplyBackend, ISettingsHost, IDisposable
    {
        private readonly AddInPaths _paths;
        private readonly Func<string, IEmbedder> _embedderFactory;
        private readonly Func<LlmProfile, string, ILlmProvider> _llmFactory;
        private readonly object _buildLock = new object();
        private readonly object _fieldLock = new object();
        private readonly List<KnowledgeSession> _retired = new List<KnowledgeSession>();
        private KnowledgeSession _session;
        private AppSettings _settings;
        private bool _nativeLoaded;

        public AddInServices(AddInPaths paths, Func<string, string> getEnv = null, Func<string, IEmbedder> embedderFactory = null,
            Func<LlmProfile, string, ILlmProvider> llmFactory = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            GetEnv = getEnv ?? EnvironmentVariables.Get;
            _embedderFactory = embedderFactory ?? (dir => new OnnxEmbedder(dir));
            _llmFactory = llmFactory ?? LlmProviderFactory.Create;
            Log = new FileLog(paths.LogDirectory);
            Directory.CreateDirectory(paths.SettingsDirectory);
            SettingsStore = new SettingsStore(paths.SettingsDirectory);
            Secrets = new SecretStore(paths.SettingsDirectory);
            _settings = SettingsStore.Load();
            if (DefaultProfileSeeder.SeedFromEnvironment(_settings, GetEnv))
            {
                SettingsStore.Save(_settings);
                Log.Info($"환경 변수에서 LLM 프로필 {_settings.Profiles.Count}개를 만들었습니다.");
            }
            Log.Cleanup();
        }

        public FileLog Log { get; }
        public SettingsStore SettingsStore { get; }
        public SecretStore Secrets { get; }
        public Func<string, string> GetEnv { get; }

        public AppSettings Settings
        {
            get { lock (_fieldLock) return _settings; }
        }

        public void ApplySettings(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            SettingsStore.Save(settings);
            lock (_fieldLock)
            {
                _settings = settings;
                if (_session != null) _retired.Add(_session);
                _session = null;
            }
            Log.Info("설정을 저장했습니다.");
        }

        public KnowledgeSession GetSession()
        {
            lock (_fieldLock)
                if (_session != null) return _session;
            lock (_buildLock)
            {
                AppSettings settings;
                lock (_fieldLock)
                {
                    if (_session != null) return _session;
                    settings = _settings;
                }
                var built = BuildSession(settings);
                lock (_fieldLock)
                {
                    if (ReferenceEquals(settings, _settings)) _session = built;
                    else _retired.Add(built);   // 만드는 사이 설정이 바뀌었으면 이번 호출에만 쓴다
                }
                return built;
            }
        }

        public ILlmProvider CreateLlm(string profileId)
        {
            var s = Settings;
            var profile = s.FindProfile(profileId) ?? s.FindProfile(s.DefaultProfileId) ?? s.Profiles.FirstOrDefault();
            if (profile == null)
                throw new LlmException(LlmErrorKind.NotConfigured, "등록된 LLM 프로필이 없습니다. [설정] → LLM 프로필에서 프로필을 추가하세요.");
            return new LlmProviderFactory(Secrets, GetEnv, _llmFactory).Create(profile);
        }

        public ILlmProvider CreateLlmForTest(LlmProfile profile, string apiKey) => _llmFactory(profile, apiKey);

        public async Task<string> SyncNowAsync(CancellationToken ct)
        {
            var session = await Task.Run(() => GetSession(), ct).ConfigureAwait(false);
            if (session.Sync == null) return "지식 폴더(RAG 루트)가 설정되지 않았습니다.";
            var ids = session.Catalog.Products.Select(p => p.Id).ToList();
            var r = await Task.Run(() => session.Sync.Sync(ids, ct), ct).ConfigureAwait(false);
            var sb = new StringBuilder(r.SharedReachable ? "공유 폴더와 동기화했습니다." : "공유 폴더에 접근할 수 없어 기존 캐시를 사용합니다.");
            sb.Append(r.UpdatedProducts.Count > 0 ? " 갱신: " + string.Join(", ", r.UpdatedProducts) : " 변경 없음.");
            foreach (var w in r.Warnings) sb.Append("\n⚠ ").Append(w);
            Log.Info("수동 동기화: " + sb.ToString().Replace('\n', ' '));
            return sb.ToString();
        }

        public IndexManifest LoadLocalManifest()
        {
            var root = (Settings.RagRoot ?? "").Trim();
            return root.Length == 0 ? null : new IndexCacheSync(root, _paths.CacheDirectory).LoadLocalManifest();
        }

        public void Dispose()
        {
            lock (_fieldLock)
            {
                _session?.Dispose();
                foreach (var s in _retired) s.Dispose();
                _retired.Clear();
                _session = null;
            }
        }

        private KnowledgeSession BuildSession(AppSettings settings)
        {
            var warnings = new List<string>();
            var root = (settings.RagRoot ?? "").Trim();
            if (root.Length == 0)
            {
                warnings.Add("지식 폴더(RAG 루트)가 설정되지 않아 RAG 없이 생성합니다. [설정] → 지식 폴더에서 지정하세요.");
                return new KnowledgeSession(ProductCatalog.CreateDefault(), null, null, warnings);
            }

            EnsureNativeLibraries();
            var sync = new IndexCacheSync(root, _paths.CacheDirectory);
            var catalog = LoadCatalog(root, sync, warnings);
            var retriever = new KnowledgeRetriever(sync, catalog, manifest =>
            {
                var dir = sync.EnsureModel(manifest?.EmbeddingModel, CancellationToken.None);
                return dir == null ? null : _embedderFactory(dir);
            });
            string Guide(ProductDefinition p)
            {
                var path = sync.LocalPromptPath(p.Id);
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            }
            return new KnowledgeSession(catalog, retriever, Guide, warnings, retriever) { Sync = sync };
        }

        private ProductCatalog LoadCatalog(string root, IndexCacheSync sync, List<string> warnings)
        {
            try
            {
                var shared = KbLayout.ProductsPath(root);
                if (File.Exists(shared)) return ProductCatalog.Load(shared);
            }
            catch (Exception ex)
            {
                warnings.Add("공유 폴더의 products.json을 읽지 못했습니다: " + ex.Message);
                Log.Warn("공유 products.json 읽기 실패: " + ex.Message);
            }
            try
            {
                if (File.Exists(sync.LocalProductsPath)) return ProductCatalog.Load(sync.LocalProductsPath);
            }
            catch (Exception ex)
            {
                Log.Warn("캐시 products.json 읽기 실패: " + ex.Message);
            }
            warnings.Add("제품 목록(products.json)을 찾지 못해 기본 제품 목록을 사용합니다.");
            return ProductCatalog.CreateDefault();
        }

        private void EnsureNativeLibraries()
        {
            if (_nativeLoaded || _paths.NativeDirectory == null) return;
            _nativeLoaded = true;
            foreach (var w in NativeLibraryPreloader.Preload(_paths.NativeDirectory)) Log.Warn(w);
        }
    }
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~AddInServicesTests"`
Expected: PASS. `RagRoot_LoadsSharedProductsJson...` 테스트는 `KnowledgeRetriever` 생성자가 공유 폴더에 즉시 접근하지 않는 것을 전제로 한다. 생성자에서 예외가 나면 `KnowledgeRetriever` 생성자를 읽고, 테스트 KB에 `_index` 폴더가 없어도 되는지 확인해 테스트 준비를 맞춘다.

- [ ] **Step 5: 커밋**

```bash
git add src/TechSupportReply.App tests/TechSupportReply.Tests/App/AddInServicesTests.cs
git commit -m "feat(app): 조립 루트 AddInServices와 지식 세션(지연 생성, 설정 변경 시 재구성)"
```

---

### Task 7: 작업창 프레젠터

**Files:**
- Create: `src/TechSupportReply.App/Pane/IReplyPaneView.cs`, `src/TechSupportReply.App/Pane/ReplyPanePresenter.cs`
- Create: `tests/TechSupportReply.Tests/TestSupport/FakePaneView.cs`, `tests/TechSupportReply.Tests/TestSupport/FakeBackend.cs`
- Test: `tests/TechSupportReply.Tests/App/ReplyPanePresenterTests.cs`

**Interfaces:**
- Consumes: Task 6 `IReplyBackend`, `KnowledgeSession`; Plan A `ProductClassifier`, `ReplyRequest`, `ReplyGenerator.GenerateAsync`
- Produces:
  - `enum PaneState { Idle, Classifying, Generating }`
  - `interface IReplyPaneView { event EventHandler GenerateRequested, StopRequested, DraftRequested, ProductChangedByUser; void ShowMail(string subject, string sender); void SetProducts(IReadOnlyList<ProductDefinition> products, string selectedId); void SelectProduct(string productId); void SetClassification(string text); void SetProfiles(IReadOnlyList<LlmProfile> profiles, string selectedId); void SetState(PaneState state); void ClearReply(); void AppendReply(string delta); string ReplyText {get;set;} void SetReferences(IReadOnlyList<string> citations); void SetWarnings(IReadOnlyList<string> warnings); void SetStatus(string message, bool isError); string SelectedProductId {get;} string SelectedProfileId {get;} string ExtraInstruction {get;} bool UseRag {get;} void Post(Action action); }`
  - `sealed class ReplyPanePresenter { ReplyPanePresenter(IReplyPaneView view, IReplyBackend backend); event Action<string> DraftReady; PaneState State; Task LoadMailAsync(MailSnapshot mail); Task GenerateAsync(); void Stop(); void RequestDraft(); }`

- [ ] **Step 1: 테스트 도우미 작성**

`tests/TechSupportReply.Tests/TestSupport/FakePaneView.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TechSupportReply.App.Pane;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Tests.TestSupport
{
    /// <summary>호출을 기록하는 뷰. Post는 즉시 실행하고, 어느 스레드에서 호출되어도 되도록 잠근다.</summary>
    internal sealed class FakePaneView : IReplyPaneView
    {
        private readonly object _lock = new object();
        private readonly StringBuilder _reply = new StringBuilder();

        public event EventHandler GenerateRequested;
        public event EventHandler StopRequested;
        public event EventHandler DraftRequested;
        public event EventHandler ProductChangedByUser;

        public string Subject { get; private set; }
        public List<string> ProductIds { get; private set; } = new List<string>();
        public string Classification { get; private set; }
        public List<string> ProfileIds { get; private set; } = new List<string>();
        public PaneState State { get; private set; }
        public List<PaneState> States { get; } = new List<PaneState>();
        public List<string> References { get; private set; } = new List<string>();
        public List<string> Warnings { get; private set; } = new List<string>();
        public string Status { get; private set; }
        public bool StatusIsError { get; private set; }
        public int ShowMailThreadId { get; private set; }

        public string SelectedProductId { get; set; }
        public string SelectedProfileId { get; set; }
        public string ExtraInstruction { get; set; } = "";
        public bool UseRag { get; set; } = true;

        public void ShowMail(string subject, string sender)
        {
            lock (_lock) { Subject = subject; ShowMailThreadId = Environment.CurrentManagedThreadId; }
        }

        public void SetProducts(IReadOnlyList<ProductDefinition> products, string selectedId)
        {
            lock (_lock) { ProductIds = products.Select(p => p.Id).ToList(); SelectedProductId = selectedId; }
        }

        public void SelectProduct(string productId)
        {
            lock (_lock) SelectedProductId = productId;
        }

        public void SetClassification(string text)
        {
            lock (_lock) Classification = text;
        }

        public void SetProfiles(IReadOnlyList<LlmProfile> profiles, string selectedId)
        {
            lock (_lock) { ProfileIds = profiles.Select(p => p.Id).ToList(); SelectedProfileId = selectedId; }
        }

        public void SetState(PaneState state)
        {
            lock (_lock) { State = state; States.Add(state); }
        }

        public void ClearReply()
        {
            lock (_lock) _reply.Clear();
        }

        public void AppendReply(string delta)
        {
            lock (_lock) _reply.Append(delta);
        }

        public string ReplyText
        {
            get { lock (_lock) return _reply.ToString(); }
            set { lock (_lock) { _reply.Clear(); _reply.Append(value); } }
        }

        public void SetReferences(IReadOnlyList<string> citations)
        {
            lock (_lock) References = citations.ToList();
        }

        public void SetWarnings(IReadOnlyList<string> warnings)
        {
            lock (_lock) Warnings = warnings.ToList();
        }

        public void SetStatus(string message, bool isError)
        {
            lock (_lock) { Status = message; StatusIsError = isError; }
        }

        public void Post(Action action) => action();

        public void UserChangesProduct(string id)
        {
            SelectedProductId = id;
            ProductChangedByUser?.Invoke(this, EventArgs.Empty);
        }

        public void ClickGenerate() => GenerateRequested?.Invoke(this, EventArgs.Empty);
        public void ClickStop() => StopRequested?.Invoke(this, EventArgs.Empty);
        public void ClickDraft() => DraftRequested?.Invoke(this, EventArgs.Empty);
    }
}
```

`tests/TechSupportReply.Tests/TestSupport/FakeBackend.cs`:
```csharp
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
            Log = new FileLog(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tsr-tests", "logs"));
        }

        public FakeLlmProvider Llm { get; }
        public FakeRetriever Retriever { get; }
        public AppSettings Settings { get; }
        public FileLog Log { get; }
        public Exception CreateLlmThrows { get; set; }
        /// <summary>설정하면 GetSession이 이 이벤트가 신호될 때까지 막힌다(느린 UNC 흉내).</summary>
        public ManualResetEventSlim SessionGate { get; set; }
        public int GetSessionThreadId { get; private set; }
        public List<string> WarningsForSession { get; } = new List<string>();

        public KnowledgeSession GetSession()
        {
            GetSessionThreadId = Environment.CurrentManagedThreadId;
            SessionGate?.Wait(TimeSpan.FromSeconds(10));
            return new KnowledgeSession(ProductCatalog.CreateDefault(), Retriever, p => "", WarningsForSession);
        }

        public ILlmProvider CreateLlm(string profileId)
        {
            if (CreateLlmThrows != null) throw CreateLlmThrows;
            return Llm;
        }
    }
}
```

- [ ] **Step 2: 실패하는 테스트 작성**

`tests/TechSupportReply.Tests/App/ReplyPanePresenterTests.cs`:
```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.App.Pane;
using TechSupportReply.Core.Knowledge;
using TechSupportReply.Core.Llm;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class ReplyPanePresenterTests
    {
        private const string DynaJson = "{\"productId\":\"ls-dyna\",\"confidence\":0.9,\"reason\":\"LS-DYNA 접촉 문의\"}";
        private static readonly global::TechSupportReply.Core.Models.MailSnapshot Mail =
            Mails.Create("접촉 경고 문의", "*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE 초기 관통 경고가 d3hsp에 나옵니다.");

        [Fact]
        public async Task LoadMail_ClassifiesWithLlm_AndSelectsProduct()
        {
            var view = new FakePaneView();
            var p = new ReplyPanePresenter(view, new FakeBackend(new FakeLlmProvider().Enqueue(DynaJson)));
            await p.LoadMailAsync(Mail);

            Assert.Equal("접촉 경고 문의", view.Subject);
            Assert.Contains("ls-dyna", view.ProductIds);
            Assert.Equal("ls-dyna", view.SelectedProductId);
            Assert.Contains("0.90", view.Classification);
            Assert.Equal("p1", view.SelectedProfileId);
            Assert.Equal(PaneState.Idle, view.State);
            Assert.False(view.StatusIsError);
        }

        [Fact]
        public async Task LoadMail_BuildsSessionOffUiThread_AndShowsMailImmediately()
        {
            var view = new FakePaneView();
            var backend = new FakeBackend(new FakeLlmProvider().Enqueue(DynaJson)) { SessionGate = new ManualResetEventSlim(false) };
            var p = new ReplyPanePresenter(view, backend);
            var callerThread = Environment.CurrentManagedThreadId;

            var task = p.LoadMailAsync(Mail);
            Assert.False(task.IsCompleted);
            Assert.Equal("접촉 경고 문의", view.Subject);
            Assert.Equal(PaneState.Classifying, view.State);

            backend.SessionGate.Set();
            await task;
            Assert.NotEqual(callerThread, backend.GetSessionThreadId);
        }

        [Fact]
        public async Task LoadMail_NoUsableLlm_FallsBackToKeywords()
        {
            var view = new FakePaneView();
            var backend = new FakeBackend(new FakeLlmProvider())
            {
                CreateLlmThrows = new LlmException(LlmErrorKind.NotConfigured, "키 없음"),
            };
            await new ReplyPanePresenter(view, backend).LoadMailAsync(Mail);
            Assert.Equal("ls-dyna", view.SelectedProductId);
            Assert.Contains("키워드", view.Classification);
            Assert.Contains("키 없음", view.Status);
        }

        [Fact]
        public async Task UserProductChoice_BeforeLlmFinishes_IsKept()
        {
            var view = new FakePaneView();
            var llm = new FakeLlmProvider { Delay = TimeSpan.FromMilliseconds(300) }.Enqueue(DynaJson);
            var p = new ReplyPanePresenter(view, new FakeBackend(llm));
            var task = p.LoadMailAsync(Mail);
            await Task.Delay(100);
            view.UserChangesProduct("ansys-fluent");
            await task;
            Assert.Equal("ansys-fluent", view.SelectedProductId);
        }

        [Fact]
        public async Task Generate_StreamsReply_ShowsReferencesAndWarnings()
        {
            var view = new FakePaneView();
            var retriever = new FakeRetriever();
            retriever.Result.References.Add(new KnowledgeChunk { ProductId = "ls-dyna", SourceFile = "manuals/contact.pdf", Page = 12, Text = "SOFT=2" });
            var backend = new FakeBackend(new FakeLlmProvider().Enqueue(DynaJson).Enqueue("안녕하세요.\nSOFT=2를 권장합니다."), retriever);
            backend.WarningsForSession.Add("세션 경고");
            var p = new ReplyPanePresenter(view, backend);
            await p.LoadMailAsync(Mail);

            await p.GenerateAsync();

            Assert.Equal("안녕하세요.\nSOFT=2를 권장합니다.", view.ReplyText);
            Assert.Equal(new[] { "contact.pdf p.12" }, view.References);
            Assert.Contains("세션 경고", view.Warnings);
            Assert.Equal("ls-dyna", retriever.Calls.Single().ProductId);
            Assert.Equal(PaneState.Idle, view.State);
            Assert.Contains(PaneState.Generating, view.States);
            Assert.False(view.StatusIsError);
        }

        [Fact]
        public async Task Generate_UsesSelectedProductAndInstruction()
        {
            var view = new FakePaneView();
            var retriever = new FakeRetriever();
            var llm = new FakeLlmProvider().Enqueue(DynaJson).Enqueue("답");
            var p = new ReplyPanePresenter(view, new FakeBackend(llm, retriever));
            await p.LoadMailAsync(Mail);
            view.UserChangesProduct("ansys-fluent");
            view.ExtraInstruction = "영어로 답해 주세요";

            await p.GenerateAsync();

            Assert.Equal("ansys-fluent", retriever.Calls.Single().ProductId);
            Assert.Contains("영어로 답해 주세요", string.Join("\n", llm.Requests.Last().Messages.Select(m => m.Text)));
        }

        [Fact]
        public async Task Generate_WithoutRag_DoesNotSearch()
        {
            var view = new FakePaneView { UseRag = false };
            var retriever = new FakeRetriever();
            var p = new ReplyPanePresenter(view, new FakeBackend(new FakeLlmProvider().Enqueue(DynaJson).Enqueue("답"), retriever));
            await p.LoadMailAsync(Mail);
            await p.GenerateAsync();
            Assert.Empty(retriever.Calls);
        }

        [Fact]
        public async Task Stop_CancelsGeneration_WithoutError()
        {
            var view = new FakePaneView();
            var llm = new FakeLlmProvider().Enqueue(DynaJson);
            var p = new ReplyPanePresenter(view, new FakeBackend(llm));
            await p.LoadMailAsync(Mail);
            llm.Delay = TimeSpan.FromSeconds(5);
            llm.Enqueue("늦은 답");

            var gen = p.GenerateAsync();
            await Task.Delay(100);
            view.ClickStop();
            await gen;

            Assert.Contains("중지", view.Status);
            Assert.False(view.StatusIsError);
            Assert.Equal("", view.ReplyText);
            Assert.Equal(PaneState.Idle, view.State);
        }

        [Fact]
        public async Task Generate_WhileGenerating_IsIgnored()
        {
            var view = new FakePaneView();
            var llm = new FakeLlmProvider().Enqueue(DynaJson);
            var p = new ReplyPanePresenter(view, new FakeBackend(llm));
            await p.LoadMailAsync(Mail);
            llm.Delay = TimeSpan.FromMilliseconds(200);
            llm.Enqueue("답");
            var requestsBefore = llm.Requests.Count;

            var first = p.GenerateAsync();
            var second = p.GenerateAsync();
            await Task.WhenAll(first, second);

            Assert.Equal(requestsBefore + 1, llm.Requests.Count);
            Assert.Equal("답", view.ReplyText);
        }

        [Fact]
        public async Task LoadingAnotherMail_CancelsGeneration_AndIgnoresStaleOutput()
        {
            var view = new FakePaneView();
            var llm = new FakeLlmProvider().Enqueue(DynaJson);
            var p = new ReplyPanePresenter(view, new FakeBackend(llm));
            await p.LoadMailAsync(Mail);
            llm.Delay = TimeSpan.FromMilliseconds(500);
            llm.Enqueue("이전 메일 답변");

            var gen = p.GenerateAsync();
            await Task.Delay(100);
            llm.Delay = TimeSpan.Zero;
            llm.Enqueue(DynaJson);
            await p.LoadMailAsync(Mails.Create("두 번째 메일", "Fluent 발산"));
            await gen;

            Assert.Equal("두 번째 메일", view.Subject);
            Assert.Equal("", view.ReplyText);
            Assert.Equal(PaneState.Idle, view.State);
        }

        [Fact]
        public async Task Generate_LlmError_ShowsUserMessage()
        {
            var view = new FakePaneView();
            var llm = new FakeLlmProvider().Enqueue(DynaJson);
            var p = new ReplyPanePresenter(view, new FakeBackend(llm));
            await p.LoadMailAsync(Mail);
            llm.ThrowOnCall = new LlmException(LlmErrorKind.RateLimited, "429");

            await p.GenerateAsync();

            Assert.True(view.StatusIsError);
            Assert.Equal(new LlmException(LlmErrorKind.RateLimited, "429").UserMessage, view.Status);
            Assert.Equal(PaneState.Idle, view.State);
        }

        [Fact]
        public async Task Generate_WithoutMail_ShowsHint()
        {
            var view = new FakePaneView();
            await new ReplyPanePresenter(view, new FakeBackend(new FakeLlmProvider())).GenerateAsync();
            Assert.True(view.StatusIsError);
            Assert.Contains("메일", view.Status);
        }

        [Fact]
        public async Task RequestDraft_RaisesEditedText_OrRejectsEmpty()
        {
            var view = new FakePaneView();
            var p = new ReplyPanePresenter(view, new FakeBackend(new FakeLlmProvider().Enqueue(DynaJson)));
            string drafted = null;
            p.DraftReady += t => drafted = t;
            await p.LoadMailAsync(Mail);

            view.ClickDraft();
            Assert.Null(drafted);
            Assert.True(view.StatusIsError);

            view.ReplyText = "  수정한 답변  ";
            view.ClickDraft();
            Assert.Equal("수정한 답변", drafted);
            Assert.False(view.StatusIsError);
        }

        [Fact]
        public async Task RequestDraft_HandlerFailure_ShowsError()
        {
            var view = new FakePaneView();
            var p = new ReplyPanePresenter(view, new FakeBackend(new FakeLlmProvider().Enqueue(DynaJson)));
            p.DraftReady += t => throw new InvalidOperationException("원본 메일을 찾을 수 없습니다");
            await p.LoadMailAsync(Mail);
            view.ReplyText = "답";
            view.ClickDraft();
            Assert.True(view.StatusIsError);
            Assert.Contains("원본 메일을 찾을 수 없습니다", view.Status);
        }
    }
}
```
(`LlmMessage`의 텍스트 속성 이름이 `Text`가 아니면 `src/TechSupportReply.Core/Llm/LlmModels.cs`를 확인해 맞춘다.)

- [ ] **Step 3: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~ReplyPanePresenterTests"`
Expected: 컴파일 오류

- [ ] **Step 4: 구현**

`src/TechSupportReply.App/Pane/IReplyPaneView.cs`:
```csharp
using System;
using System.Collections.Generic;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.App.Pane
{
    public enum PaneState
    {
        Idle,
        Classifying,
        Generating,
    }

    /// <summary>작업창 화면 계약. 프레젠터는 UI 스레드에서 호출하고, 백그라운드 콜백은 Post로 넘긴다.</summary>
    public interface IReplyPaneView
    {
        event EventHandler GenerateRequested;
        event EventHandler StopRequested;
        event EventHandler DraftRequested;
        /// <summary>사용자가 드롭다운에서 제품을 직접 바꿨을 때만 발생한다(코드로 선택할 때는 발생하지 않음).</summary>
        event EventHandler ProductChangedByUser;

        void ShowMail(string subject, string sender);
        void SetProducts(IReadOnlyList<ProductDefinition> products, string selectedId);
        void SelectProduct(string productId);
        void SetClassification(string text);
        void SetProfiles(IReadOnlyList<LlmProfile> profiles, string selectedId);
        void SetState(PaneState state);
        void ClearReply();
        void AppendReply(string delta);
        /// <summary>줄바꿈은 \n으로 주고받는다.</summary>
        string ReplyText { get; set; }
        void SetReferences(IReadOnlyList<string> citations);
        void SetWarnings(IReadOnlyList<string> warnings);
        void SetStatus(string message, bool isError);

        string SelectedProductId { get; }
        string SelectedProfileId { get; }
        string ExtraInstruction { get; }
        bool UseRag { get; }

        void Post(Action action);
    }
}
```

`src/TechSupportReply.App/Pane/ReplyPanePresenter.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Generation;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Models;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.App.Pane
{
    /// <summary>
    /// 작업창 흐름: 메일 표시 → 제품군 판별(키워드 즉시, LLM 후속) → 사용자 확인·변경 → 스트리밍 생성 → 회신 초안 요청.
    /// UI 스레드에서 호출한다. 공유 폴더·LLM 작업은 Task.Run으로 넘기고, 메일이 바뀌면 이전 작업을 취소·무시한다.
    /// </summary>
    public sealed class ReplyPanePresenter
    {
        private static readonly IReadOnlyList<string> None = new string[0];
        private readonly IReplyPaneView _view;
        private readonly IReplyBackend _backend;
        private CancellationTokenSource _mailCts;
        private CancellationTokenSource _generateCts;
        private MailSnapshot _mail;
        private int _mailVersion;
        private bool _userChoseProduct;

        public ReplyPanePresenter(IReplyPaneView view, IReplyBackend backend)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _view.GenerateRequested += (s, e) => _ = GenerateAsync();
            _view.StopRequested += (s, e) => Stop();
            _view.DraftRequested += (s, e) => RequestDraft();
            _view.ProductChangedByUser += (s, e) => _userChoseProduct = true;
        }

        /// <summary>사용자가 검토·수정한 답변 텍스트(\n 줄바꿈)로 회신 초안을 만들어 달라는 요청.</summary>
        public event Action<string> DraftReady;

        public PaneState State { get; private set; } = PaneState.Idle;

        public async Task LoadMailAsync(MailSnapshot mail)
        {
            if (mail == null) throw new ArgumentNullException(nameof(mail));
            _mailCts?.Cancel();
            var cts = _mailCts = new CancellationTokenSource();
            var version = ++_mailVersion;
            _mail = mail;
            _userChoseProduct = false;

            _view.ShowMail(mail.Subject, string.IsNullOrEmpty(mail.SenderEmail) ? mail.SenderName : $"{mail.SenderName} <{mail.SenderEmail}>");
            _view.ClearReply();
            _view.SetReferences(None);
            _view.SetWarnings(None);
            SetState(PaneState.Classifying);
            _view.SetStatus("제품군을 판별하는 중…", false);

            var settings = _backend.Settings;
            _view.SetProfiles(settings.Profiles, ResolveProfile(settings, settings.DefaultProfileId)?.Id);
            try
            {
                var session = await Task.Run(() => _backend.GetSession(), cts.Token);
                if (version != _mailVersion) return;

                var classifier = new ProductClassifier(session.Catalog);
                var keyword = classifier.ClassifyByKeywords(mail);
                _view.SetProducts(session.Catalog.Products, keyword.ProductId);
                _view.SetClassification(Describe(session.Catalog, keyword));

                ILlmProvider llm = null;
                string llmProblem = null;
                if (ResolveProfile(settings, settings.ClassifierProfileId) != null)
                {
                    try { llm = _backend.CreateLlm(ResolveProfile(settings, settings.ClassifierProfileId).Id); }
                    catch (LlmException ex) { llmProblem = ex.UserMessage; }
                }
                else
                {
                    llmProblem = "LLM 프로필이 없습니다. [설정]에서 프로필을 추가하세요.";
                }

                var result = llm == null ? keyword : await Task.Run(() => classifier.ClassifyAsync(mail, llm, cts.Token), cts.Token);
                if (version != _mailVersion) return;
                if (!_userChoseProduct) _view.SelectProduct(result.ProductId);
                _view.SetClassification(Describe(session.Catalog, result));
                if (llmProblem != null) _view.SetStatus("키워드로 제품군을 판별했습니다. " + llmProblem, true);
                else _view.SetStatus("제품군을 확인하거나 바꾼 뒤 [답변 생성]을 누르세요.", false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (version != _mailVersion) return;
                _backend.Log.Error("제품군 판별 실패", ex);
                if (_view.SelectedProductId == null) _view.SetProducts(ProductCatalog.CreateDefault().Products, ProductCatalog.CommonId);
                _view.SetStatus("제품군 판별 실패: " + ex.Message + " — 제품군을 직접 선택하세요.", true);
            }
            finally
            {
                if (version == _mailVersion && State == PaneState.Classifying) SetState(PaneState.Idle);
            }
        }

        public async Task GenerateAsync()
        {
            if (State != PaneState.Idle) return;
            if (_mail == null)
            {
                _view.SetStatus("메일을 먼저 선택한 뒤 리본의 [기술지원 답변]을 누르세요.", true);
                return;
            }

            var version = _mailVersion;
            _generateCts?.Dispose();
            var cts = _generateCts = CancellationTokenSource.CreateLinkedTokenSource(_mailCts?.Token ?? CancellationToken.None);
            SetState(PaneState.Generating);
            _view.ClearReply();
            _view.SetReferences(None);
            _view.SetWarnings(None);
            _view.SetStatus("답변을 생성하는 중… (중지하려면 [중지])", false);

            var request = new ReplyRequest
            {
                Mail = _mail,
                ProductId = _view.SelectedProductId ?? ProductCatalog.CommonId,
                ExtraInstruction = _view.ExtraInstruction ?? "",
                UseRag = _view.UseRag,
            };
            var profileId = _view.SelectedProfileId;
            try
            {
                var llm = _backend.CreateLlm(profileId);
                var session = await Task.Run(() => _backend.GetSession(), cts.Token);
                var generator = session.CreateGenerator(_backend.Settings);
                void OnDelta(string delta)
                {
                    if (cts.IsCancellationRequested) return;
                    _view.Post(() =>
                    {
                        if (version == _mailVersion && !cts.IsCancellationRequested) _view.AppendReply(delta);
                    });
                }
                var result = await Task.Run(() => generator.GenerateAsync(request, llm, OnDelta, cts.Token), cts.Token);
                if (version != _mailVersion) return;

                _view.ReplyText = result.Text;
                _view.SetReferences(result.References.Select(r => r.Citation).Distinct().ToList());
                _view.SetWarnings(session.Warnings.Concat(result.Warnings).Distinct().ToList());
                _view.SetStatus("생성 완료 — 내용을 검토·수정한 뒤 [회신 초안 만들기]를 누르세요.", false);
            }
            catch (OperationCanceledException)
            {
                if (version == _mailVersion) _view.SetStatus("생성을 중지했습니다.", false);
            }
            catch (LlmException ex)
            {
                _backend.Log.Error($"답변 생성 실패({ex.Kind})", ex);
                if (version == _mailVersion) _view.SetStatus(ex.UserMessage, true);
            }
            catch (Exception ex)
            {
                _backend.Log.Error("답변 생성 실패", ex);
                if (version == _mailVersion) _view.SetStatus("답변 생성 중 오류: " + ex.Message, true);
            }
            finally
            {
                if (version == _mailVersion) SetState(PaneState.Idle);
            }
        }

        public void Stop() => _generateCts?.Cancel();

        public void RequestDraft()
        {
            if (State != PaneState.Idle) return;
            var text = (_view.ReplyText ?? "").Trim();
            if (text.Length == 0)
            {
                _view.SetStatus("회신에 넣을 답변이 없습니다. 먼저 [답변 생성]을 누르세요.", true);
                return;
            }
            try
            {
                DraftReady?.Invoke(text);
                _view.SetStatus("회신 초안을 열었습니다. 검토한 뒤 직접 발송하세요.", false);
            }
            catch (Exception ex)
            {
                _backend.Log.Error("회신 초안 생성 실패", ex);
                _view.SetStatus("회신 초안을 만들지 못했습니다: " + ex.Message, true);
            }
        }

        private void SetState(PaneState state)
        {
            State = state;
            _view.SetState(state);
        }

        private static LlmProfile ResolveProfile(AppSettings s, string id) =>
            s.FindProfile(id) ?? s.FindProfile(s.DefaultProfileId) ?? s.Profiles.FirstOrDefault();

        private static string Describe(ProductCatalog catalog, ClassificationResult r)
        {
            var name = catalog.Find(r.ProductId)?.DisplayName ?? r.ProductId;
            var source = r.Source == ClassificationSource.Llm ? "LLM" : "키워드";
            var reason = string.IsNullOrWhiteSpace(r.Reason) ? "" : " — " + r.Reason;
            return $"{name} · 신뢰도 {r.Confidence:0.00} · {source}{reason}";
        }
    }
}
```
(`ClassificationSource`의 키워드 값 이름이 `Keyword`가 아니거나 `Describe`가 기본 결과에서 "키워드"를 만들지 못하면, `ClassificationResult.cs`를 확인해 맞춘다. 테스트는 키워드 대체 시 분류 문구에 "키워드"가 들어가는 것만 요구한다.)

- [ ] **Step 5: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~ReplyPanePresenterTests"`
Expected: PASS (타이밍 테스트가 간헐적으로 실패하면 지연값을 늘리지 말고 원인을 찾는다. `superpowers:systematic-debugging`을 쓴다)

- [ ] **Step 6: 커밋**

```bash
git add src/TechSupportReply.App/Pane tests/TechSupportReply.Tests
git commit -m "feat(app): 작업창 프레젠터(분류→변경→스트리밍 생성→초안, 취소·재진입 방지)"
```

---

### Task 8: 작업창 WinForms 컨트롤

**Files:**
- Create: `src/TechSupportReply.App/Pane/ReplyTaskPaneControl.cs`
- Test: `tests/TechSupportReply.Tests/App/ReplyTaskPaneControlTests.cs`

**Interfaces:**
- Consumes: Task 7 `IReplyPaneView`, `PaneState`
- Produces: `sealed class ReplyTaskPaneControl : UserControl, IReplyPaneView { event EventHandler SettingsRequested; internal Button GenerateButton, StopButton, DraftButton; internal ComboBox ProductCombo, ProfileCombo; internal TextBox ReplyBox; internal ListBox ReferenceList; internal Label StatusLabel; }`

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
using System;
using System.Linq;
using TechSupportReply.App.Pane;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class ReplyTaskPaneControlTests
    {
        [Fact]
        public void ReplyText_RoundTripsNewlines()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    c.AppendReply("첫 줄\n둘");
                    c.AppendReply("째 줄");
                    Assert.Equal("첫 줄\r\n둘째 줄", c.ReplyBox.Text);
                    Assert.Equal("첫 줄\n둘째 줄", c.ReplyText);
                    c.ReplyText = "a\nb";
                    Assert.Equal("a\r\nb", c.ReplyBox.Text);
                }
            });
        }

        [Fact]
        public void Products_ProgrammaticSelection_DoesNotRaiseUserEvent()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    int userChanges = 0;
                    c.ProductChangedByUser += (s, e) => userChanges++;
                    c.SetProducts(ProductCatalog.CreateDefault().Products, "ls-dyna");
                    Assert.Equal("ls-dyna", c.SelectedProductId);
                    c.SelectProduct("ansys-fluent");
                    Assert.Equal("ansys-fluent", c.SelectedProductId);
                    Assert.Equal(0, userChanges);
                }
            });
        }

        [Fact]
        public void Profiles_SelectedId()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    var a = new LlmProfile { Id = "a", DisplayName = "A" };
                    var b = new LlmProfile { Id = "b", DisplayName = "B" };
                    c.SetProfiles(new[] { a, b }, "b");
                    Assert.Equal("b", c.SelectedProfileId);
                    Assert.Equal(new[] { "A", "B" }, c.ProfileCombo.Items.Cast<object>().Select(o => o.ToString()));
                }
            });
        }

        [Theory]
        [InlineData(PaneState.Idle, true, false)]
        [InlineData(PaneState.Classifying, false, false)]
        [InlineData(PaneState.Generating, false, true)]
        public void SetState_TogglesButtons(PaneState state, bool generateEnabled, bool stopEnabled)
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    c.SetState(state);
                    Assert.Equal(generateEnabled, c.GenerateButton.Enabled);
                    Assert.Equal(stopEnabled, c.StopButton.Enabled);
                    Assert.Equal(state == PaneState.Idle, c.DraftButton.Enabled);
                }
            });
        }

        [Fact]
        public void Buttons_RaiseEvents_AndStatusShowsError()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    int gen = 0, stop = 0, draft = 0;
                    c.GenerateRequested += (s, e) => gen++;
                    c.StopRequested += (s, e) => stop++;
                    c.DraftRequested += (s, e) => draft++;
                    c.SetState(PaneState.Idle);
                    c.GenerateButton.PerformClick();
                    c.DraftButton.PerformClick();
                    c.SetState(PaneState.Generating);
                    c.StopButton.PerformClick();
                    Assert.Equal((1, 1, 1), (gen, stop, draft));

                    c.SetStatus("오류", true);
                    Assert.Equal("오류", c.StatusLabel.Text);
                    c.SetReferences(new[] { "a.pdf p.1" });
                    Assert.Equal(1, c.ReferenceList.Items.Count);
                    Assert.True(c.UseRag);
                }
            });
        }

        [Fact]
        public void Post_WithoutHandle_RunsInline()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    bool ran = false;
                    c.Post(() => ran = true);
                    Assert.True(ran);
                }
            });
        }
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~ReplyTaskPaneControlTests"`
Expected: 컴파일 오류

- [ ] **Step 3: 구현**

`src/TechSupportReply.App/Pane/ReplyTaskPaneControl.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.App.Pane
{
    /// <summary>Outlook 오른쪽 작업창. 디자이너 없이 코드로 배치한다.</summary>
    public sealed class ReplyTaskPaneControl : UserControl, IReplyPaneView
    {
        private sealed class Item
        {
            public Item(string id, string text) { Id = id; Text = text; }
            public string Id { get; }
            public string Text { get; }
            public override string ToString() => Text;
        }

        private readonly Label _subject = new Label { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold) };
        private readonly Label _sender = new Label { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
        private readonly Label _classification = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, MaximumSize = new Size(1000, 0) };
        private readonly TextBox _instruction = new TextBox { Multiline = true, Height = 44, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        private readonly CheckBox _useRag = new CheckBox { Text = "지식 폴더(RAG) 검색 사용", Checked = true, AutoSize = true };
        private readonly Label _warnings = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DarkOrange, MaximumSize = new Size(1000, 0) };
        private readonly LinkLabel _settings = new LinkLabel { Text = "설정…", AutoSize = true, Anchor = AnchorStyles.Right };
        private PaneState _state = PaneState.Idle;

        internal readonly ComboBox ProductCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        internal readonly ComboBox ProfileCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        internal readonly Button GenerateButton = new Button { Text = "답변 생성", AutoSize = true };
        internal readonly Button StopButton = new Button { Text = "중지", AutoSize = true, Enabled = false };
        internal readonly Button DraftButton = new Button { Text = "회신 초안 만들기", AutoSize = true };
        internal readonly TextBox ReplyBox = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true, WordWrap = true };
        internal readonly ListBox ReferenceList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, Height = 80 };
        internal readonly Label StatusLabel = new Label { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(1000, 0) };

        public ReplyTaskPaneControl()
        {
            Font = new Font("맑은 고딕", 9f);
            Padding = new Padding(6);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            void Row(Control left, Control right, SizeType sizeType = SizeType.AutoSize, float height = 0)
            {
                layout.RowStyles.Add(new RowStyle(sizeType, height));
                var row = layout.RowCount++;
                if (right == null)
                {
                    layout.Controls.Add(left, 0, row);
                    layout.SetColumnSpan(left, 2);
                }
                else
                {
                    layout.Controls.Add(left, 0, row);
                    layout.Controls.Add(right, 1, row);
                }
            }
            Label Caption(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 22, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(_subject, 0, 0);
            header.Controls.Add(_settings, 1, 0);
            Row(header, null, SizeType.Absolute, 24);
            Row(_sender, null, SizeType.Absolute, 20);
            Row(Caption("제품군"), ProductCombo);
            Row(_classification, null);
            Row(Caption("LLM 프로필"), ProfileCombo);
            Row(_useRag, null);
            Row(Caption("추가 지시"), _instruction);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
            buttons.Controls.AddRange(new Control[] { GenerateButton, StopButton, DraftButton });
            Row(buttons, null);
            Row(ReplyBox, null, SizeType.Percent, 100);
            Row(Caption("참고 문서"), null);
            Row(ReferenceList, null, SizeType.Absolute, 84);
            Row(_warnings, null);
            Row(StatusLabel, null);
            Controls.Add(layout);

            GenerateButton.Click += (s, e) => GenerateRequested?.Invoke(this, EventArgs.Empty);
            StopButton.Click += (s, e) => StopRequested?.Invoke(this, EventArgs.Empty);
            DraftButton.Click += (s, e) => DraftRequested?.Invoke(this, EventArgs.Empty);
            ProductCombo.SelectionChangeCommitted += (s, e) => ProductChangedByUser?.Invoke(this, EventArgs.Empty);
            _settings.LinkClicked += (s, e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            ShowMail("메일을 선택한 뒤 리본의 [기술지원 답변]을 누르세요.", "");
            SetState(PaneState.Idle);
        }

        public event EventHandler GenerateRequested;
        public event EventHandler StopRequested;
        public event EventHandler DraftRequested;
        public event EventHandler ProductChangedByUser;
        public event EventHandler SettingsRequested;

        public string SelectedProductId => (ProductCombo.SelectedItem as Item)?.Id;
        public string SelectedProfileId => (ProfileCombo.SelectedItem as Item)?.Id;
        public string ExtraInstruction => _instruction.Text.Trim();
        public bool UseRag => _useRag.Checked;

        public string ReplyText
        {
            get => ReplyBox.Text.Replace("\r\n", "\n");
            set => ReplyBox.Text = ToWindowsNewlines(value);
        }

        public void ShowMail(string subject, string sender)
        {
            _subject.Text = subject ?? "";
            _sender.Text = sender ?? "";
        }

        public void SetProducts(IReadOnlyList<ProductDefinition> products, string selectedId)
        {
            ProductCombo.BeginUpdate();
            ProductCombo.Items.Clear();
            foreach (var p in products) ProductCombo.Items.Add(new Item(p.Id, p.DisplayName));
            ProductCombo.EndUpdate();
            SelectProduct(selectedId);
        }

        public void SelectProduct(string productId) => Select(ProductCombo, productId);

        public void SetClassification(string text) => _classification.Text = text ?? "";

        public void SetProfiles(IReadOnlyList<LlmProfile> profiles, string selectedId)
        {
            ProfileCombo.BeginUpdate();
            ProfileCombo.Items.Clear();
            foreach (var p in profiles) ProfileCombo.Items.Add(new Item(p.Id, p.DisplayName));
            ProfileCombo.EndUpdate();
            Select(ProfileCombo, selectedId);
        }

        public void SetState(PaneState state)
        {
            _state = state;
            var idle = state == PaneState.Idle;
            GenerateButton.Enabled = idle;
            DraftButton.Enabled = idle;
            StopButton.Enabled = state == PaneState.Generating;
            ProductCombo.Enabled = state != PaneState.Generating;
            ProfileCombo.Enabled = idle;
            ReplyBox.ReadOnly = state == PaneState.Generating;
            UseWaitCursor = state != PaneState.Idle;
        }

        public void ClearReply() => ReplyBox.Clear();

        public void AppendReply(string delta) => ReplyBox.AppendText(ToWindowsNewlines(delta));

        public void SetReferences(IReadOnlyList<string> citations)
        {
            ReferenceList.BeginUpdate();
            ReferenceList.Items.Clear();
            foreach (var c in citations) ReferenceList.Items.Add(c);
            ReferenceList.EndUpdate();
        }

        public void SetWarnings(IReadOnlyList<string> warnings) =>
            _warnings.Text = warnings.Count == 0 ? "" : string.Join("\n", warnings.Select(w => "⚠ " + w));

        public void SetStatus(string message, bool isError)
        {
            StatusLabel.Text = message ?? "";
            StatusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.ControlText;
        }

        public void Post(Action action)
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
            else if (!IsDisposed) action();
        }

        private static void Select(ComboBox combo, string id)
        {
            var item = combo.Items.Cast<Item>().FirstOrDefault(i => i.Id == id);
            combo.SelectedItem = item ?? (combo.Items.Count > 0 ? combo.Items[0] : null);
        }

        private static string ToWindowsNewlines(string text) => (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~ReplyTaskPaneControlTests"`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/TechSupportReply.App/Pane/ReplyTaskPaneControl.cs tests/TechSupportReply.Tests/App/ReplyTaskPaneControlTests.cs
git commit -m "feat(app): 작업창 WinForms 컨트롤"
```

---

### Task 9: 설정 편집기(순수 로직)

**Files:**
- Create: `src/TechSupportReply.App/SettingsUi/SettingsEditor.cs`
- Test: `tests/TechSupportReply.Tests/App/SettingsEditorTests.cs`

**Interfaces:**
- Consumes: Task 2 `ProfilePresets`, Plan A `SecretStore`, `JsonDefaults.Options`, `AnthropicProvider.EnsureSupportedModel`
- Produces: `sealed class SettingsEditor { SettingsEditor(AppSettings current, SecretStore secrets); AppSettings Working; static AppSettings Clone(AppSettings); LlmProfile AddProfile(ProfilePreset preset); void RemoveProfile(string id); void SetPendingKey(string profileId, string key); bool HasStoredKey(LlmProfile p); bool HasPendingKey(string profileId); string ResolveKey(LlmProfile p, Func<string,string> getEnv); IReadOnlyList<string> Validate(); AppSettings Commit(); }`

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
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
    }
}
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~SettingsEditorTests"`
Expected: 컴파일 오류

- [ ] **Step 3: 구현**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Serialization;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.App.SettingsUi
{
    /// <summary>
    /// 설정 대화상자의 편집 상태. 원본 설정의 사본(Working)을 고치고, [저장] 시 Commit에서
    /// 새 API 키를 DPAPI에 쓰고 삭제된 프로필의 키를 지운다. 취소하면 아무것도 바뀌지 않는다.
    /// </summary>
    public sealed class SettingsEditor
    {
        private readonly SecretStore _secrets;
        private readonly Dictionary<string, string> _pendingKeys = new Dictionary<string, string>();
        private readonly HashSet<string> _removedSecretIds = new HashSet<string>();

        public SettingsEditor(AppSettings current, SecretStore secrets)
        {
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            Working = Clone(current ?? new AppSettings());
        }

        public AppSettings Working { get; }

        public static AppSettings Clone(AppSettings s) =>
            JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonDefaults.Options), JsonDefaults.Options);

        public LlmProfile AddProfile(ProfilePreset preset)
        {
            var p = ProfilePresets.Create(preset);
            p.DisplayName = UniqueName(p.DisplayName);
            Working.Profiles.Add(p);
            if (Working.FindProfile(Working.DefaultProfileId) == null) Working.DefaultProfileId = p.Id;
            if (Working.FindProfile(Working.ClassifierProfileId) == null) Working.ClassifierProfileId = p.Id;
            return p;
        }

        public void RemoveProfile(string id)
        {
            var p = Working.FindProfile(id);
            if (p == null) return;
            Working.Profiles.Remove(p);
            _pendingKeys.Remove(id);
            if (!string.IsNullOrEmpty(p.SecretId)) _removedSecretIds.Add(p.SecretId);
            var fallback = Working.Profiles.FirstOrDefault()?.Id ?? "";
            if (Working.DefaultProfileId == id) Working.DefaultProfileId = fallback;
            if (Working.ClassifierProfileId == id) Working.ClassifierProfileId = fallback;
        }

        public void SetPendingKey(string profileId, string key) => _pendingKeys[profileId] = (key ?? "").Trim();

        public bool HasPendingKey(string profileId) => _pendingKeys.TryGetValue(profileId, out var k) && k.Length > 0;

        public bool HasStoredKey(LlmProfile p)
        {
            if (p == null || string.IsNullOrEmpty(p.SecretId)) return false;
            try { return !string.IsNullOrEmpty(_secrets.Get(p.SecretId)); }
            catch (CryptographicException) { return false; }
        }

        /// <summary>[연결 테스트]용 키: 환경 변수(값이 있을 때) → 입력 중인 키 → 저장된 키.</summary>
        public string ResolveKey(LlmProfile p, Func<string, string> getEnv)
        {
            if (!string.IsNullOrWhiteSpace(p.ApiKeyEnvVar))
            {
                var fromEnv = getEnv?.Invoke(p.ApiKeyEnvVar.Trim());
                if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
            }
            if (HasPendingKey(p.Id)) return _pendingKeys[p.Id];
            return HasStoredKey(p) ? _secrets.Get(p.SecretId) : null;
        }

        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            foreach (var p in Working.Profiles)
            {
                var name = string.IsNullOrWhiteSpace(p.DisplayName) ? "(이름 없음)" : p.DisplayName.Trim();
                if (string.IsNullOrWhiteSpace(p.DisplayName)) errors.Add("프로필 이름이 비어 있습니다.");
                if (string.IsNullOrWhiteSpace(p.Model)) errors.Add($"'{name}': 모델명을 입력하세요.");
                else if (p.Provider == LlmProviderKind.Anthropic)
                {
                    try { AnthropicProvider.EnsureSupportedModel(p.Model.Trim()); }
                    catch (ArgumentException ex) { errors.Add($"'{name}': {ex.Message}"); }
                }
                if (!string.IsNullOrWhiteSpace(p.BaseUrl)
                    && (!Uri.TryCreate(p.BaseUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                    errors.Add($"'{name}': Base URL 형식이 올바르지 않습니다(예: https://api.x.ai/v1).");
                if (p.MaxTokens < 256 || p.MaxTokens > 128000) errors.Add($"'{name}': 최대 토큰은 256~128000 사이여야 합니다.");
            }
            foreach (var dup in Working.Profiles.GroupBy(p => (p.DisplayName ?? "").Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0 && g.Count() > 1))
                errors.Add($"프로필 이름 '{dup.Key}'이(가) 중복됩니다.");
            if (Working.ReferenceTopK < 1 || Working.ReferenceTopK > 30) errors.Add("근거 문서 수는 1~30 사이여야 합니다.");
            if (Working.StyleExampleTopK < 0 || Working.StyleExampleTopK > 10) errors.Add("문체 예시 수는 0~10 사이여야 합니다.");
            return errors;
        }

        public AppSettings Commit()
        {
            foreach (var kv in _pendingKeys)
            {
                var p = Working.FindProfile(kv.Key);
                if (p == null || kv.Value.Length == 0) continue;
                if (string.IsNullOrEmpty(p.SecretId)) p.SecretId = SecretStore.NewId();
                _secrets.Set(p.SecretId, kv.Value);
            }
            foreach (var id in _removedSecretIds) _secrets.Remove(id);
            _pendingKeys.Clear();
            _removedSecretIds.Clear();
            return Clone(Working);
        }

        private string UniqueName(string baseName)
        {
            var name = baseName;
            for (int i = 2; Working.Profiles.Any(p => string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)); i++)
                name = baseName + " " + i;
            return name;
        }
    }
}
```
(`TechSupportReply.App.Settings` 네임스페이스는 만들지 않는다. `Core.Settings`와 이름이 겹쳐 혼동되므로 폴더와 네임스페이스 모두 `SettingsUi`를 쓴다.)

- [ ] **Step 4: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~SettingsEditorTests"`
Expected: PASS

- [ ] **Step 5: 커밋**

```bash
git add src/TechSupportReply.App/SettingsUi/SettingsEditor.cs tests/TechSupportReply.Tests/App/SettingsEditorTests.cs
git commit -m "feat(app): 설정 편집기(프로필 CRUD, 키 보류·커밋, 검증)"
```

---

### Task 10: 설정 대화상자(WinForms)

**Files:**
- Create: `src/TechSupportReply.App/SettingsUi/SettingsForm.cs`
- Test: `tests/TechSupportReply.Tests/App/SettingsFormTests.cs`

**Interfaces:**
- Consumes: Task 2 `LlmConnectionTester`, Task 6 `ISettingsHost`, Task 9 `SettingsEditor`
- Produces: `sealed class SettingsForm : Form { SettingsForm(ISettingsHost host); internal SettingsEditor Editor; internal ListBox ProfileList; internal TabControl Tabs; internal bool TrySave(out IReadOnlyList<string> errors); internal void AddPreset(ProfilePreset preset); }`

탭 구성:
- **사용자**: 이름, 직함, 회사, 기본 어조
- **LLM 프로필**: 왼쪽 목록과 [Claude 추가][OpenAI 추가][xAI 추가][삭제] 버튼. 오른쪽 편집기: 표시명, 공급자(Anthropic / OpenAI 호환), 모델, Base URL, 최대 토큰, Effort(Anthropic만), 키 방식("직접 입력(암호화 저장)"은 비밀번호 상자와 "저장됨" 표시, "환경 변수"는 변수명 입력), Workspace ID(Anthropic만), [연결 테스트]와 결과 줄. 아래: 기본 답변 프로필, 분류 프로필
- **지식 폴더**: RAG 루트와 [찾아보기], 근거 문서 수, 문체 예시 수, [지금 동기화]와 결과. 색인 상태 ListView(제품·버전·빌드 시각·파일 수·청크 수)
- 하단: [저장][취소]

- [ ] **Step 1: 실패하는 테스트 작성**

```csharp
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
                        Assert.Equal(1, f.ProfileList.Items.Count);
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
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~SettingsFormTests"`
Expected: 컴파일 오류

- [ ] **Step 3: 구현**

`src/TechSupportReply.App/SettingsUi/SettingsForm.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.App.SettingsUi
{
    /// <summary>설정 대화상자. 편집은 SettingsEditor의 사본에만 반영하고 [저장]에서 한 번에 적용한다.</summary>
    public sealed class SettingsForm : Form
    {
        private readonly ISettingsHost _host;
        private bool _loadingProfile;

        // 사용자
        private readonly TextBox _userName = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _userTitle = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _userCompany = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _userTone = new TextBox { Dock = DockStyle.Fill };

        // 프로필
        private readonly TextBox _pName = new TextBox { Dock = DockStyle.Fill };
        private readonly ComboBox _pProvider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TextBox _pModel = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _pBaseUrl = new TextBox { Dock = DockStyle.Fill };
        private readonly NumericUpDown _pMaxTokens = new NumericUpDown { Minimum = 256, Maximum = 128000, Increment = 1000, Dock = DockStyle.Left, Width = 100 };
        private readonly ComboBox _pEffort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left, Width = 100 };
        private readonly RadioButton _pKeyDirect = new RadioButton { Text = "직접 입력(DPAPI 암호화 저장)", AutoSize = true };
        private readonly RadioButton _pKeyEnv = new RadioButton { Text = "환경 변수에서 읽기", AutoSize = true };
        private readonly TextBox _pKey = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        private readonly Label _pKeyState = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly TextBox _pEnvVar = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _pWorkspace = new TextBox { Dock = DockStyle.Fill };
        private readonly Button _pTest = new Button { Text = "연결 테스트", AutoSize = true };
        private readonly Label _pTestResult = new Label { AutoSize = true, MaximumSize = new Size(420, 0) };
        private readonly ComboBox _defaultProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox _classifierProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly Panel _profileEditor = new Panel { Dock = DockStyle.Fill };

        // 지식 폴더
        private readonly TextBox _ragRoot = new TextBox { Dock = DockStyle.Fill };
        private readonly NumericUpDown _refTopK = new NumericUpDown { Minimum = 1, Maximum = 30, Dock = DockStyle.Left, Width = 80 };
        private readonly NumericUpDown _styleTopK = new NumericUpDown { Minimum = 0, Maximum = 10, Dock = DockStyle.Left, Width = 80 };
        private readonly Button _syncNow = new Button { Text = "지금 동기화", AutoSize = true };
        private readonly Label _syncResult = new Label { AutoSize = true, MaximumSize = new Size(560, 0) };
        private readonly ListView _indexStatus = new ListView { View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };

        internal readonly TabControl Tabs = new TabControl { Dock = DockStyle.Fill };
        internal readonly ListBox ProfileList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

        public SettingsForm(ISettingsHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            Editor = new SettingsEditor(host.Settings, host.Secrets);
            Text = "기술지원 답변 — 설정";
            Font = new Font("맑은 고딕", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(780, 600);
            MinimumSize = new Size(700, 520);

            Tabs.TabPages.Add(BuildUserTab());
            Tabs.TabPages.Add(BuildProfilesTab());
            Tabs.TabPages.Add(BuildKnowledgeTab());

            var save = new Button { Text = "저장", AutoSize = true };
            var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(save);
            Controls.Add(Tabs);
            Controls.Add(bottom);
            CancelButton = cancel;
            save.Click += (s, e) =>
            {
                if (TrySave(out var errors)) { DialogResult = DialogResult.OK; Close(); }
                else MessageBox.Show(this, string.Join("\n", errors), "설정을 확인하세요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            LoadUser();
            RefreshProfileList(Editor.Working.Profiles.FirstOrDefault()?.Id);
            LoadKnowledge();
        }

        internal SettingsEditor Editor { get; }

        internal bool TrySave(out IReadOnlyList<string> errors)
        {
            StoreUser();
            StoreProfile();
            StoreKnowledge();
            errors = Editor.Validate();
            if (errors.Count > 0) return false;
            _host.ApplySettings(Editor.Commit());
            return true;
        }

        internal void AddPreset(ProfilePreset preset)
        {
            StoreProfile();
            var p = Editor.AddProfile(preset);
            RefreshProfileList(p.Id);
        }

        // ---------- 사용자 ----------
        private TabPage BuildUserTab()
        {
            var t = Grid();
            AddRow(t, "이름", _userName);
            AddRow(t, "직함", _userTitle);
            AddRow(t, "회사", _userCompany);
            AddRow(t, "기본 어조", _userTone);
            AddRow(t, "", new Label { Text = "답변 서명과 어조에 사용합니다.", AutoSize = true, ForeColor = SystemColors.GrayText });
            return Page("사용자", t);
        }

        private void LoadUser()
        {
            var u = Editor.Working.User;
            _userName.Text = u.Name;
            _userTitle.Text = u.Title;
            _userCompany.Text = u.Company;
            _userTone.Text = u.Tone;
        }

        private void StoreUser()
        {
            var u = Editor.Working.User;
            u.Name = _userName.Text.Trim();
            u.Title = _userTitle.Text.Trim();
            u.Company = _userCompany.Text.Trim();
            u.Tone = _userTone.Text.Trim();
        }

        // ---------- 프로필 ----------
        private TabPage BuildProfilesTab()
        {
            _pProvider.Items.AddRange(new object[] { "Anthropic (Claude)", "OpenAI 호환 (OpenAI·xAI 등)" });
            _pEffort.Items.AddRange(new object[] { "low", "medium", "high", "max" });

            var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = 210, RowCount = 2 };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var addButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var addClaude = new Button { Text = "Claude 추가", AutoSize = true };
            var addOpenAi = new Button { Text = "OpenAI 추가", AutoSize = true };
            var addXai = new Button { Text = "xAI 추가", AutoSize = true };
            var remove = new Button { Text = "삭제", AutoSize = true };
            addButtons.Controls.AddRange(new Control[] { addClaude, addOpenAi, addXai, remove });
            left.Controls.Add(ProfileList, 0, 0);
            left.Controls.Add(addButtons, 0, 1);

            var editor = Grid();
            AddRow(editor, "표시명", _pName);
            AddRow(editor, "공급자", _pProvider);
            AddRow(editor, "모델", _pModel);
            AddRow(editor, "Base URL", _pBaseUrl);
            AddRow(editor, "", new Label { Text = "비워 두면 공급자 기본값. xAI는 https://api.x.ai/v1", AutoSize = true, ForeColor = SystemColors.GrayText });
            AddRow(editor, "최대 토큰", _pMaxTokens);
            AddRow(editor, "Effort", _pEffort);
            AddRow(editor, "API 키", _pKeyDirect);
            AddRow(editor, "", _pKey);
            AddRow(editor, "", _pKeyState);
            AddRow(editor, "", _pKeyEnv);
            AddRow(editor, "변수 이름", _pEnvVar);
            AddRow(editor, "Workspace ID", _pWorkspace);
            AddRow(editor, "", new Label { Text = "워크스페이스에 속하지 않은 Anthropic 키만 필요합니다(wrkspc_…).", AutoSize = true, ForeColor = SystemColors.GrayText });
            AddRow(editor, "", _pTest);
            AddRow(editor, "", _pTestResult);
            _profileEditor.Controls.Add(editor);

            var defaults = Grid();
            defaults.Dock = DockStyle.Bottom;
            defaults.AutoSize = true;
            AddRow(defaults, "기본 답변 프로필", _defaultProfile);
            AddRow(defaults, "제품 분류 프로필", _classifierProfile);

            var page = new TabPage("LLM 프로필") { Padding = new Padding(6) };
            page.Controls.Add(_profileEditor);
            page.Controls.Add(left);
            page.Controls.Add(defaults);

            ProfileList.SelectedIndexChanged += (s, e) =>
            {
                if (_loadingProfile) return;
                StoreProfileById(_currentProfileId);
                LoadProfile(SelectedProfile());
            };
            addClaude.Click += (s, e) => AddPreset(ProfilePreset.Claude);
            addOpenAi.Click += (s, e) => AddPreset(ProfilePreset.OpenAI);
            addXai.Click += (s, e) => AddPreset(ProfilePreset.Xai);
            remove.Click += (s, e) =>
            {
                var p = SelectedProfile();
                if (p == null) return;
                if (MessageBox.Show(this, $"'{p.DisplayName}' 프로필을 삭제할까요? 저장된 키도 삭제됩니다.", "프로필 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                _currentProfileId = null;
                Editor.RemoveProfile(p.Id);
                RefreshProfileList(Editor.Working.Profiles.FirstOrDefault()?.Id);
            };
            _pProvider.SelectedIndexChanged += (s, e) => UpdateProviderFields();
            _pKeyDirect.CheckedChanged += (s, e) => UpdateKeyFields();
            _pName.Leave += (s, e) => { StoreProfile(); RefreshProfileList(_currentProfileId); };
            _pTest.Click += async (s, e) =>
            {
                StoreProfile();
                var p = SelectedProfile();
                if (p == null) return;
                var key = Editor.ResolveKey(p, _host.GetEnv);
                if (string.IsNullOrWhiteSpace(key)) { ShowTest(false, "API 키가 없습니다. 키를 입력하거나 환경 변수를 확인하세요."); return; }
                _pTest.Enabled = false;
                ShowTest(true, "확인 중…");
                try
                {
                    var llm = _host.CreateLlmForTest(p, key);
                    var r = await LlmConnectionTester.TestAsync(llm, CancellationToken.None);
                    ShowTest(r.Success, r.Message);
                }
                catch (Exception ex)
                {
                    ShowTest(false, ex is LlmException le ? le.UserMessage : ex.Message);
                }
                finally
                {
                    _pTest.Enabled = true;
                }
            };
            return page;
        }

        private string _currentProfileId;

        private LlmProfile SelectedProfile() => (ProfileList.SelectedItem as ProfileItem)?.Profile;

        private sealed class ProfileItem
        {
            public ProfileItem(LlmProfile p) { Profile = p; }
            public LlmProfile Profile { get; }
            public override string ToString() => Profile.DisplayName;
        }

        private void RefreshProfileList(string selectId)
        {
            _loadingProfile = true;
            ProfileList.BeginUpdate();
            ProfileList.Items.Clear();
            foreach (var p in Editor.Working.Profiles) ProfileList.Items.Add(new ProfileItem(p));
            ProfileList.EndUpdate();
            var item = ProfileList.Items.Cast<ProfileItem>().FirstOrDefault(i => i.Profile.Id == selectId);
            ProfileList.SelectedItem = item;
            _loadingProfile = false;
            LoadProfile(item?.Profile);
            RefreshDefaultCombos();
        }

        private void RefreshDefaultCombos()
        {
            foreach (var (combo, id) in new[] { (_defaultProfile, Editor.Working.DefaultProfileId), (_classifierProfile, Editor.Working.ClassifierProfileId) })
            {
                combo.Items.Clear();
                foreach (var p in Editor.Working.Profiles) combo.Items.Add(new ProfileItem(p));
                combo.SelectedItem = combo.Items.Cast<ProfileItem>().FirstOrDefault(i => i.Profile.Id == id);
            }
        }

        private void LoadProfile(LlmProfile p)
        {
            _currentProfileId = p?.Id;
            _profileEditor.Enabled = p != null;
            _pTestResult.Text = "";
            if (p == null) return;
            _pName.Text = p.DisplayName;
            _pProvider.SelectedIndex = p.Provider == LlmProviderKind.Anthropic ? 0 : 1;
            _pModel.Text = p.Model;
            _pBaseUrl.Text = p.BaseUrl;
            _pMaxTokens.Value = Math.Max(_pMaxTokens.Minimum, Math.Min(_pMaxTokens.Maximum, p.MaxTokens));
            _pEffort.SelectedItem = string.IsNullOrEmpty(p.Effort) ? "medium" : p.Effort;
            _pKey.Text = "";
            _pEnvVar.Text = p.ApiKeyEnvVar;
            _pWorkspace.Text = p.WorkspaceId;
            _pKeyEnv.Checked = !string.IsNullOrWhiteSpace(p.ApiKeyEnvVar);
            _pKeyDirect.Checked = !_pKeyEnv.Checked;
            _pKeyState.Text = Editor.HasPendingKey(p.Id) ? "새 키 입력됨(저장 시 적용)" : Editor.HasStoredKey(p) ? "저장된 키가 있습니다. 바꾸려면 새 키를 입력하세요." : "저장된 키가 없습니다.";
            UpdateProviderFields();
            UpdateKeyFields();
        }

        private void StoreProfile() => StoreProfileById(_currentProfileId);

        private void StoreProfileById(string id)
        {
            var p = id == null ? null : Editor.Working.FindProfile(id);
            if (p != null)
            {
                p.DisplayName = _pName.Text.Trim();
                p.Provider = _pProvider.SelectedIndex == 0 ? LlmProviderKind.Anthropic : LlmProviderKind.OpenAI;
                p.Model = _pModel.Text.Trim();
                p.BaseUrl = _pBaseUrl.Text.Trim();
                p.MaxTokens = (int)_pMaxTokens.Value;
                p.Effort = (_pEffort.SelectedItem as string) ?? "medium";
                p.ApiKeyEnvVar = _pKeyEnv.Checked ? _pEnvVar.Text.Trim() : "";
                p.WorkspaceId = _pWorkspace.Text.Trim();
                if (_pKeyDirect.Checked && _pKey.Text.Trim().Length > 0) Editor.SetPendingKey(p.Id, _pKey.Text);
            }
            if (_defaultProfile.SelectedItem is ProfileItem d) Editor.Working.DefaultProfileId = d.Profile.Id;
            if (_classifierProfile.SelectedItem is ProfileItem c) Editor.Working.ClassifierProfileId = c.Profile.Id;
        }

        private void UpdateProviderFields()
        {
            var anthropic = _pProvider.SelectedIndex == 0;
            _pEffort.Enabled = anthropic;
            _pWorkspace.Enabled = anthropic;
        }

        private void UpdateKeyFields()
        {
            _pKey.Enabled = _pKeyDirect.Checked;
            _pEnvVar.Enabled = _pKeyEnv.Checked;
        }

        private void ShowTest(bool ok, string message)
        {
            _pTestResult.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
            _pTestResult.Text = message;
        }

        // ---------- 지식 폴더 ----------
        private TabPage BuildKnowledgeTab()
        {
            var browse = new Button { Text = "찾아보기…", AutoSize = true };
            var rootRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
            rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rootRow.Controls.Add(_ragRoot, 0, 0);
            rootRow.Controls.Add(browse, 1, 0);

            var t = Grid();
            AddRow(t, "RAG 루트", rootRow);
            AddRow(t, "", new Label { Text = @"예: \\server\KB — 관리자가 Indexer로 색인을 만든 공유 폴더", AutoSize = true, ForeColor = SystemColors.GrayText });
            AddRow(t, "근거 문서 수", _refTopK);
            AddRow(t, "문체 예시 수", _styleTopK);
            AddRow(t, "", _syncNow);
            AddRow(t, "", _syncResult);
            t.Dock = DockStyle.Top;
            t.AutoSize = true;

            foreach (var (text, width) in new[] { ("제품", 160), ("버전", 60), ("빌드 시각", 150), ("파일", 60), ("청크", 70) })
                _indexStatus.Columns.Add(text, width);

            var page = new TabPage("지식 폴더") { Padding = new Padding(6) };
            page.Controls.Add(_indexStatus);
            page.Controls.Add(t);

            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "공유 지식 폴더(RAG 루트)를 선택하세요.", SelectedPath = _ragRoot.Text })
                    if (dlg.ShowDialog(this) == DialogResult.OK) _ragRoot.Text = dlg.SelectedPath;
            };
            _syncNow.Click += async (s, e) =>
            {
                if (!string.Equals((_host.Settings.RagRoot ?? "").Trim(), _ragRoot.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _syncResult.Text = "RAG 루트를 바꿨습니다. 먼저 [저장]한 뒤 다시 여세요.";
                    return;
                }
                _syncNow.Enabled = false;
                _syncResult.Text = "동기화 중…";
                try
                {
                    _syncResult.Text = await _host.SyncNowAsync(CancellationToken.None);
                    LoadIndexStatus();
                }
                catch (Exception ex)
                {
                    _syncResult.Text = "동기화 실패: " + ex.Message;
                }
                finally
                {
                    _syncNow.Enabled = true;
                }
            };
            return page;
        }

        private void LoadKnowledge()
        {
            _ragRoot.Text = Editor.Working.RagRoot;
            _refTopK.Value = Math.Max(_refTopK.Minimum, Math.Min(_refTopK.Maximum, Editor.Working.ReferenceTopK));
            _styleTopK.Value = Math.Max(_styleTopK.Minimum, Math.Min(_styleTopK.Maximum, Editor.Working.StyleExampleTopK));
            LoadIndexStatus();
        }

        private void LoadIndexStatus()
        {
            _indexStatus.Items.Clear();
            try
            {
                var manifest = _host.LoadLocalManifest();
                if (manifest == null) return;
                foreach (var p in manifest.Products)
                    _indexStatus.Items.Add(new ListViewItem(new[]
                    {
                        p.ProductId, p.Version.ToString(CultureInfo.InvariantCulture), p.BuiltAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        p.FileCount.ToString(CultureInfo.InvariantCulture), p.ChunkCount.ToString(CultureInfo.InvariantCulture),
                    }));
            }
            catch (Exception ex)
            {
                _syncResult.Text = "색인 상태를 읽지 못했습니다: " + ex.Message;
            }
        }

        private void StoreKnowledge()
        {
            Editor.Working.RagRoot = _ragRoot.Text.Trim();
            Editor.Working.ReferenceTopK = (int)_refTopK.Value;
            Editor.Working.StyleExampleTopK = (int)_styleTopK.Value;
        }

        // ---------- 배치 도우미 ----------
        private static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(4) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        private static void AddRow(TableLayoutPanel t, string caption, Control control)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var row = t.RowCount++;
            t.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, row);
            t.Controls.Add(control, 1, row);
        }

        private static TabPage Page(string title, Control content)
        {
            var page = new TabPage(title) { Padding = new Padding(6) };
            page.Controls.Add(content);
            return page;
        }
    }
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~SettingsFormTests"`
Expected: PASS. 이어서 전체 테스트 `dotnet test tests/TechSupportReply.Tests`

- [ ] **Step 5: 커밋**

```bash
git add src/TechSupportReply.App/SettingsUi/SettingsForm.cs tests/TechSupportReply.Tests/App/SettingsFormTests.cs
git commit -m "feat(app): 설정 대화상자(사용자·LLM 프로필·지식 폴더, 연결 테스트, 동기화)"
```

---

### Task 11: VSTO 애드인 프로젝트 · 서명 · 빌드 스크립트

**Files:**
- Create: `tools/New-DevSigningCert.ps1`, `tools/build-addin.ps1`
- Create: `src/TechSupportReply.AddIn/TechSupportReply.AddIn.csproj`, `Properties/AssemblyInfo.cs`, `ThisAddIn.cs`, `ThisAddIn.Designer.cs`, `ThisAddIn.Designer.xml`, `TechSupportRibbon.cs`, `Outlook/MailExtractor.cs`, `Outlook/ReplyDraftWriter.cs`, `Outlook/TaskPaneManager.cs`
- Modify: `.gitignore` (`Signing.user.props`), `TechSupportReply.sln` (AddIn 추가)

**Interfaces:**
- Consumes: Task 3 `ReplyHtmlComposer`, Task 5 `RibbonMarkup`/`AttachmentTextBuilder`/`SavedAttachment`, Task 6 `AddInServices`/`AddInPaths`, Task 7 `ReplyPanePresenter`, Task 8 `ReplyTaskPaneControl`, Task 10 `SettingsForm`
- Produces: 빌드 산출물 `src/TechSupportReply.AddIn/bin/<Configuration>/TechSupportReply.AddIn.vsto`(+ `.dll.manifest`, `.dll.config`, `onnxruntime.dll`, `e_sqlite3.dll`). Debug 빌드를 하면 개발자 PC의 `HKCU\Software\Microsoft\Office\Outlook\Addins\TechSupportReply.AddIn`에 자동 등록된다.

이 Task의 COM 코드는 단위 테스트 대상이 아니다. 로직은 이미 App/Core에 있고 테스트되었다. 검증은 빌드 성공, 산출물 확인, Task 14의 E2E로 한다.

- [ ] **Step 1: 서명 인증서 스크립트**

`tools/New-DevSigningCert.ps1`:
```powershell
<#
.SYNOPSIS
  VSTO 매니페스트 서명용 코드 서명 인증서를 준비하고 src/TechSupportReply.AddIn/Signing.user.props에 지문을 기록한다.
.DESCRIPTION
  -PfxPath를 주면 회사 인증서(PFX)를 현재 사용자 저장소로 가져와 사용한다. 주지 않으면 개발용 자체 서명 인증서를 재사용하거나 새로 만든다.
  공개 인증서(.cer)는 publish\TechSupportReply-signing.cer로 내보낸다(팀 PC 신뢰 배포용).
#>
param(
    [string]$Subject = 'CN=TechSupportReply Dev (KOSTECH)',
    [string]$PfxPath,
    [securestring]$PfxPassword
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if ($PfxPath) {
    $cert = Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation Cert:\CurrentUser\My -Password $PfxPassword -Exportable
} else {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation Cert:\CurrentUser\My `
            -NotAfter (Get-Date).AddYears(5) -KeyExportPolicy Exportable -HashAlgorithm SHA256
        Write-Host "새 개발용 인증서를 만들었습니다: $($cert.Thumbprint)"
    }
}

$props = Join-Path $repo 'src\TechSupportReply.AddIn\Signing.user.props'
@"
<Project>
  <PropertyGroup>
    <ManifestCertificateThumbprint>$($cert.Thumbprint)</ManifestCertificateThumbprint>
  </PropertyGroup>
</Project>
"@ | Set-Content -Path $props -Encoding UTF8

$publish = Join-Path $repo 'publish'
New-Item -ItemType Directory -Force $publish | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $publish 'TechSupportReply-signing.cer') | Out-Null
Write-Host "서명 인증서: $($cert.Subject) / $($cert.Thumbprint)"
Write-Host "기록: $props"
```
`.gitignore`의 `*.pfx` 아래에 `Signing.user.props`를 추가한다.

- [ ] **Step 2: VSTO 프로젝트 파일**

`src/TechSupportReply.AddIn/TechSupportReply.AddIn.csproj`(스파이크로 명령줄 빌드와 서명을 검증한 구성이다):
```xml
<Project ToolsVersion="17.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" Condition="Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')" />
  <Import Project="Signing.user.props" Condition="Exists('Signing.user.props')" />
  <PropertyGroup>
    <ProjectTypeGuids>{BAA0C2D2-18E2-41B9-852F-F413020CAA33};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}</ProjectTypeGuids>
    <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
    <ProjectGuid>{6D3B2A41-9E7C-4B8F-A512-3C0E9F7D1B24}</ProjectGuid>
    <OutputType>Library</OutputType>
    <NoStandardLibraries>false</NoStandardLibraries>
    <RootNamespace>TechSupportReply.AddIn</RootNamespace>
    <AssemblyName>TechSupportReply.AddIn</AssemblyName>
    <LoadBehavior>3</LoadBehavior>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <DefineConstants>VSTO40;UseOfficeInterop</DefineConstants>
    <ResolveComReferenceSilent>true</ResolveComReferenceSilent>
    <OfficeApplication>Outlook</OfficeApplication>
    <PlatformTarget>x64</PlatformTarget>
    <RestoreProjectStyle>PackageReference</RestoreProjectStyle>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
    <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
    <SignManifests>true</SignManifests>
    <!-- ClickOnce 게시 기본값(publish-addin.ps1에서 덮어씀) -->
    <BootstrapperEnabled>false</BootstrapperEnabled>
    <UpdateEnabled>true</UpdateEnabled>
    <UpdateInterval>0</UpdateInterval>
    <UpdateIntervalUnits>days</UpdateIntervalUnits>
    <ProductName>기술지원 답변 도우미</ProductName>
    <PublisherName>KOSTECH</PublisherName>
    <FriendlyName>기술지원 답변 도우미</FriendlyName>
    <OfficeApplicationDescription>LS-DYNA·Ansys 기술지원 메일의 회신 초안을 만듭니다(자동 발송 없음).</OfficeApplicationDescription>
    <ApplicationVersion>1.0.0.%2a</ApplicationVersion>
    <IsWebBootstrapper>false</IsWebBootstrapper>
    <Install>true</Install>
    <InstallFrom>Unc</InstallFrom>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)' == 'Debug' ">
    <DebugSymbols>true</DebugSymbols>
    <DebugType>full</DebugType>
    <Optimize>false</Optimize>
    <OutputPath>bin\Debug\</OutputPath>
    <DefineConstants>$(DefineConstants);DEBUG;TRACE</DefineConstants>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)' == 'Release' ">
    <DebugType>pdbonly</DebugType>
    <Optimize>true</Optimize>
    <OutputPath>bin\Release\</OutputPath>
    <DefineConstants>$(DefineConstants);TRACE</DefineConstants>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Accessibility" />
    <Reference Include="System" />
    <Reference Include="System.Core" />
    <Reference Include="System.Data" />
    <Reference Include="System.Drawing" />
    <Reference Include="System.Windows.Forms" />
    <Reference Include="System.Xml" />
    <Reference Include="System.Xml.Linq" />
    <Reference Include="Microsoft.CSharp" />
    <Reference Include="Microsoft.Office.Tools.v4.0.Framework, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL"><Private>False</Private></Reference>
    <Reference Include="Microsoft.VisualStudio.Tools.Applications.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL"><Private>False</Private></Reference>
    <Reference Include="Microsoft.Office.Tools, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL"><Private>False</Private></Reference>
    <Reference Include="Microsoft.Office.Tools.Common, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL"><Private>False</Private></Reference>
    <Reference Include="Microsoft.Office.Tools.Outlook, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL"><Private>False</Private></Reference>
    <Reference Include="Microsoft.Office.Tools.Common.v4.0.Utilities, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL"><Private>True</Private></Reference>
    <Reference Include="Microsoft.Office.Tools.Outlook.v4.0.Utilities, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL"><Private>True</Private></Reference>
    <Reference Include="Office, Version=15.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c"><Private>False</Private><EmbedInteropTypes>true</EmbedInteropTypes></Reference>
    <Reference Include="Microsoft.Office.Interop.Outlook, Version=15.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c"><Private>False</Private><EmbedInteropTypes>true</EmbedInteropTypes></Reference>
    <Reference Include="stdole, Version=7.0.3300.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"><Private>False</Private></Reference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\TechSupportReply.Core\TechSupportReply.Core.csproj" />
    <ProjectReference Include="..\TechSupportReply.Rag\TechSupportReply.Rag.csproj" />
    <ProjectReference Include="..\TechSupportReply.App\TechSupportReply.App.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- 실행 프로젝트가 직접 참조해야 애드인 폴더에 네이티브 DLL이 복사된다(net48 함정). -->
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.30.0" />
    <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.13" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="Properties\AssemblyInfo.cs" />
    <Compile Include="ThisAddIn.cs"><SubType>Code</SubType></Compile>
    <None Include="ThisAddIn.Designer.xml"><DependentUpon>ThisAddIn.cs</DependentUpon></None>
    <Compile Include="ThisAddIn.Designer.cs"><DependentUpon>ThisAddIn.Designer.xml</DependentUpon></Compile>
    <Compile Include="TechSupportRibbon.cs" />
    <Compile Include="Outlook\MailExtractor.cs" />
    <Compile Include="Outlook\ReplyDraftWriter.cs" />
    <Compile Include="Outlook\TaskPaneManager.cs" />
  </ItemGroup>
  <PropertyGroup>
    <VisualStudioVersion Condition="'$(VisualStudioVersion)' == ''">17.0</VisualStudioVersion>
    <VSToolsPath Condition="'$(VSToolsPath)' == ''">$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)</VSToolsPath>
  </PropertyGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
  <Import Project="$(VSToolsPath)\OfficeTools\Microsoft.VisualStudio.Tools.Office.targets" Condition="'$(VSToolsPath)' != ''" />
  <Target Name="TsrCheckSigning" BeforeTargets="BeforeBuild" Condition="'$(ManifestCertificateThumbprint)' == ''">
    <Error Text="매니페스트 서명 인증서가 없습니다. PowerShell에서 tools\New-DevSigningCert.ps1을 먼저 실행하세요." />
  </Target>
  <ProjectExtensions>
    <VisualStudio>
      <FlavorProperties GUID="{BAA0C2D2-18E2-41B9-852F-F413020CAA33}">
        <ProjectProperties HostName="Outlook" HostPackage="{29A7B9D7-A7F1-4328-8EF0-6B2D1A56B2C1}" ProjectCreationSetting="1" OfficeVersion="15.0" VstxVersion="4.0" ApplicationType="Outlook" Language="cs" TemplatesPath="" AddItemTemplatesGuid="{A58A78EB-1C92-4DDD-80CF-E8BD872ABFC4}" DebugInfoExeName="#Software\Microsoft\Office\16.0\Outlook\InstallRoot\Path#outlook.exe" DebugInfoCommandLine="" DebugInfoWorkingDir="" IconImageList="" />
        <Host Name="Outlook" GeneratedCodeNamespace="TechSupportReply.AddIn" IconIndex="0">
          <HostItem Name="ThisAddIn" Code="ThisAddIn.cs" CanonicalName="ThisAddIn" Blueprint="ThisAddIn.Designer.xml" GeneratedCode="ThisAddIn.Designer.cs" IconIndex="1" />
        </Host>
      </FlavorProperties>
    </VisualStudio>
  </ProjectExtensions>
</Project>
```

- [ ] **Step 3: 템플릿에서 Designer 파일 생성**

Run(PowerShell):
```powershell
$T = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\ProjectTemplates\CSharp\Office\Addins\1042\VSTOOutlook15AddInV4"
$D = "src\TechSupportReply.AddIn"
(Get-Content "$T\ThisAddIn.Designer.xml" -Raw -Encoding UTF8).Replace('$safeprojectname$','TechSupportReply.AddIn') | Set-Content "$D\ThisAddIn.Designer.xml" -Encoding UTF8
(Get-Content "$T\ThisAddIn.Designer.cs" -Raw -Encoding UTF8).Replace('$safeprojectname$','TechSupportReply.AddIn').Replace('$clrversion$','4.0.30319.42000') | Set-Content "$D\ThisAddIn.Designer.cs" -Encoding UTF8
```
Expected: 두 파일이 생기고 `$safeprojectname$`가 남아 있지 않다(`Select-String '\$safeprojectname\$' src\TechSupportReply.AddIn\*`의 결과가 비어 있음).

`src/TechSupportReply.AddIn/Properties/AssemblyInfo.cs`:
```csharp
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("TechSupportReply.AddIn")]
[assembly: AssemblyDescription("Outlook 기술지원 답변 도우미")]
[assembly: AssemblyCompany("KOSTECH")]
[assembly: AssemblyProduct("TechSupportReply")]
[assembly: AssemblyCopyright("Copyright © KOSTECH 2026")]
[assembly: ComVisible(false)]
[assembly: Guid("b4f1e3a2-7c5d-4e89-9a61-2f3d8c0b7e45")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
```

- [ ] **Step 4: 애드인 코드 작성**

`src/TechSupportReply.AddIn/ThisAddIn.cs`:
```csharp
using System;
using System.Windows.Forms;
using TechSupportReply.App.Hosting;
using TechSupportReply.App.SettingsUi;
using TechSupportReply.AddIn.Outlook;
using Office = Microsoft.Office.Core;

namespace TechSupportReply.AddIn
{
    public partial class ThisAddIn
    {
        internal const string Title = "기술지원 답변";
        private AddInServices _services;
        private TaskPaneManager _panes;

        /// <summary>첫 사용 시 만든다. Outlook 시작 시간을 늘리지 않기 위해 Startup에서 만들지 않는다.</summary>
        internal AddInServices Services => _services ?? (_services = new AddInServices(AddInPaths.Default()));

        internal TaskPaneManager Panes => _panes ?? (_panes = new TaskPaneManager(this));

        protected override Office.IRibbonExtensibility CreateRibbonExtensibilityObject() => new TechSupportRibbon();

        internal void ShowSettings()
        {
            using (var form = new SettingsForm(Services)) form.ShowDialog();
        }

        internal void ReportError(string action, Exception ex)
        {
            try { Services.Log.Error(action + " 실패", ex); }
            catch (Exception) { }
            MessageBox.Show($"{action} 중 오류가 발생했습니다.\n{ex.Message}\n\n로그: %LOCALAPPDATA%\\TechSupportReply\\logs", Title,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            // Outlook은 빠른 종료 시 이 이벤트를 보내지 않을 수 있다. 정리는 최선 노력이다.
            try
            {
                _panes?.Dispose();
                _services?.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private void InternalStartup()
        {
            Startup += ThisAddIn_Startup;
            Shutdown += ThisAddIn_Shutdown;
        }
    }
}
```

`src/TechSupportReply.AddIn/TechSupportRibbon.cs`:
```csharp
using System;
using System.Runtime.InteropServices;
using TechSupportReply.App.Hosting;
using Office = Microsoft.Office.Core;

namespace TechSupportReply.AddIn
{
    /// <summary>리본 XML 콜백. 모든 콜백은 예외를 삼켜 Outlook이 애드인을 비활성화하지 않게 한다.</summary>
    [ComVisible(true)]
    public sealed class TechSupportRibbon : Office.IRibbonExtensibility
    {
        public string GetCustomUI(string ribbonID) => RibbonMarkup.Get(ribbonID);

        public void OnRibbonLoad(Office.IRibbonUI ribbonUI)
        {
        }

        public void OnReplyClick(Office.IRibbonControl control)
        {
            try { Globals.ThisAddIn.Panes.RunForContext(control.Context); }
            catch (Exception ex) { Globals.ThisAddIn.ReportError("기술지원 답변", ex); }
        }

        public void OnSettingsClick(Office.IRibbonControl control)
        {
            try { Globals.ThisAddIn.ShowSettings(); }
            catch (Exception ex) { Globals.ThisAddIn.ReportError("설정 열기", ex); }
        }
    }
}
```

`src/TechSupportReply.AddIn/Outlook/MailExtractor.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using TechSupportReply.App.Mail;
using TechSupportReply.Core.Diagnostics;
using TechSupportReply.Core.Models;
using OutlookApi = Microsoft.Office.Interop.Outlook;

namespace TechSupportReply.AddIn.Outlook
{
    /// <summary>UI 스레드에서 MailItem을 읽어 COM과 무관한 MailSnapshot으로 복사한다. 텍스트 첨부는 임시 폴더에 저장해 읽고 지운다.</summary>
    internal static class MailExtractor
    {
        public static MailSnapshot Extract(OutlookApi.MailItem mail, FileLog log)
        {
            var snapshot = new MailSnapshot
            {
                Subject = mail.Subject ?? "",
                Body = mail.Body ?? "",
                SenderName = mail.SenderName ?? "",
                SenderEmail = SenderAddress(mail),
                ReceivedAt = mail.ReceivedTime,
            };

            var tempDir = Path.Combine(Path.GetTempPath(), "TechSupportReply", Guid.NewGuid().ToString("N"));
            var saved = new List<SavedAttachment>();
            var attachments = mail.Attachments;
            try
            {
                for (int i = 1; i <= attachments.Count; i++)
                {
                    var a = attachments[i];
                    try
                    {
                        if (a.Type != OutlookApi.OlAttachmentType.olByValue) continue;
                        var name = a.FileName ?? "";
                        if (AttachmentTextBuilder.IsInlineImage(name)) continue;
                        snapshot.AttachmentNames.Add(name);
                        if (!AttachmentTextBuilder.IsTextCandidate(name, a.Size)) continue;
                        Directory.CreateDirectory(tempDir);
                        var path = Path.Combine(tempDir, i + "_" + AttachmentTextBuilder.SafeFileName(name));
                        a.SaveAsFile(path);
                        saved.Add(new SavedAttachment(name, path));
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"첨부 {i} 처리 실패: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(a);
                    }
                }
                snapshot.AttachmentText = AttachmentTextBuilder.Build(saved);
            }
            finally
            {
                Marshal.ReleaseComObject(attachments);
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return snapshot;
        }

        private static string SenderAddress(OutlookApi.MailItem mail)
        {
            try
            {
                if (string.Equals(mail.SenderEmailType, "EX", StringComparison.OrdinalIgnoreCase))
                {
                    var sender = mail.Sender;
                    try
                    {
                        var exchangeUser = sender?.GetExchangeUser();
                        if (exchangeUser != null)
                        {
                            try { return exchangeUser.PrimarySmtpAddress ?? ""; }
                            finally { Marshal.ReleaseComObject(exchangeUser); }
                        }
                    }
                    finally
                    {
                        if (sender != null) Marshal.ReleaseComObject(sender);
                    }
                }
                return mail.SenderEmailAddress ?? "";
            }
            catch (COMException)
            {
                return "";
            }
        }
    }
}
```

`src/TechSupportReply.AddIn/Outlook/ReplyDraftWriter.cs`:
```csharp
using System.Runtime.InteropServices;
using TechSupportReply.Core.Text;
using OutlookApi = Microsoft.Office.Interop.Outlook;

namespace TechSupportReply.AddIn.Outlook
{
    /// <summary>원본에 [전체 회신] 초안을 만들고 답변을 맨 앞에 넣어 창을 연다. 발송하지 않는다.</summary>
    internal static class ReplyDraftWriter
    {
        public static void CreateReplyAll(OutlookApi.MailItem original, string replyText)
        {
            var reply = original.ReplyAll();
            try
            {
                // 편집기(Inspector)를 먼저 만들어야 Outlook이 기본 서명을 본문에 넣는다.
                var inspector = reply.GetInspector;
                try
                {
                    if (reply.BodyFormat == OutlookApi.OlBodyFormat.olFormatPlain)
                        reply.Body = (replyText ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n\r\n" + reply.Body;
                    else
                        reply.HTMLBody = ReplyHtmlComposer.InsertAtTop(reply.HTMLBody, replyText);
                    reply.Display(false);
                }
                finally
                {
                    Marshal.ReleaseComObject(inspector);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(reply);
            }
        }
    }
}
```

`src/TechSupportReply.AddIn/Outlook/TaskPaneManager.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Office.Tools;
using TechSupportReply.App.Pane;
using TechSupportReply.Core.Models;
using OutlookApi = Microsoft.Office.Interop.Outlook;

namespace TechSupportReply.AddIn.Outlook
{
    /// <summary>Explorer/읽기 Inspector 창마다 작업창 하나를 만들고, 창이 닫히면 제거한다.</summary>
    internal sealed class TaskPaneManager : IDisposable
    {
        private sealed class PaneEntry
        {
            public CustomTaskPane Pane;
            public ReplyPanePresenter Presenter;
            public string EntryId;
        }

        private readonly ThisAddIn _addIn;
        private readonly Dictionary<object, PaneEntry> _panes = new Dictionary<object, PaneEntry>();

        public TaskPaneManager(ThisAddIn addIn)
        {
            _addIn = addIn;
        }

        public void RunForContext(object context)
        {
            var mail = GetMail(context, out var window);
            if (mail == null)
            {
                MessageBox.Show("메일을 하나 선택한 뒤 다시 누르세요.", ThisAddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            MailSnapshot snapshot;
            string entryId;
            try
            {
                snapshot = MailExtractor.Extract(mail, _addIn.Services.Log);
                entryId = mail.EntryID;
            }
            finally
            {
                Marshal.ReleaseComObject(mail);
            }

            var entry = GetOrCreate(window);
            entry.EntryId = entryId;
            entry.Pane.Visible = true;
            _ = entry.Presenter.LoadMailAsync(snapshot);
        }

        public void Dispose()
        {
            foreach (var window in _panes.Keys.ToList()) Remove(window);
        }

        private static OutlookApi.MailItem GetMail(object context, out object window)
        {
            window = context;
            if (context is OutlookApi.Explorer explorer)
            {
                var selection = explorer.Selection;
                try
                {
                    if (selection.Count < 1) return null;
                    var item = selection[1];
                    if (item is OutlookApi.MailItem m) return m;
                    Marshal.ReleaseComObject(item);
                    return null;
                }
                finally
                {
                    Marshal.ReleaseComObject(selection);
                }
            }
            if (context is OutlookApi.Inspector inspector)
                return inspector.CurrentItem as OutlookApi.MailItem;
            return null;
        }

        private PaneEntry GetOrCreate(object window)
        {
            if (_panes.TryGetValue(window, out var existing)) return existing;

            var control = new ReplyTaskPaneControl();
            var entry = new PaneEntry { Presenter = new ReplyPanePresenter(control, _addIn.Services) };
            entry.Presenter.DraftReady += text => CreateDraft(entry, text);
            control.SettingsRequested += (s, e) =>
            {
                try { _addIn.ShowSettings(); }
                catch (Exception ex) { _addIn.ReportError("설정 열기", ex); }
            };
            entry.Pane = _addIn.CustomTaskPanes.Add(control, ThisAddIn.Title, window);
            entry.Pane.Width = 460;
            _panes[window] = entry;

            if (window is OutlookApi.Inspector inspector)
                ((OutlookApi.InspectorEvents_10_Event)inspector).Close += () => Remove(window);
            else if (window is OutlookApi.Explorer explorer)
                ((OutlookApi.ExplorerEvents_10_Event)explorer).Close += () => Remove(window);
            return entry;
        }

        private void CreateDraft(PaneEntry entry, string text)
        {
            if (string.IsNullOrEmpty(entry.EntryId)) throw new InvalidOperationException("원본 메일의 ID가 없습니다. 메일을 다시 선택하세요.");
            object item = null;
            try
            {
                item = _addIn.Application.Session.GetItemFromID(entry.EntryId);
                if (!(item is OutlookApi.MailItem mail)) throw new InvalidOperationException("원본 메일을 찾을 수 없습니다(이동 또는 삭제되었을 수 있습니다).");
                ReplyDraftWriter.CreateReplyAll(mail, text);
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
            }
        }

        private void Remove(object window)
        {
            if (!_panes.TryGetValue(window, out var entry)) return;
            _panes.Remove(window);
            try
            {
                entry.Presenter.Stop();
                _addIn.CustomTaskPanes.Remove(entry.Pane);
            }
            catch (Exception ex)
            {
                _addIn.Services.Log.Warn("작업창 제거 실패: " + ex.Message);
            }
        }
    }
}
```
(`GetItemFromID`가 다른 저장소의 메일에서 실패하면 E2E에서 확인한다. 필요하면 `StoreID`도 함께 저장해 두 번째 인수로 넘긴다.)

`tools/build-addin.ps1`:
```powershell
<#
.SYNOPSIS
  VSTO 애드인을 VS 2022 MSBuild로 빌드한다(dotnet build로는 VSTO 대상 파일을 쓸 수 없다).
  Debug 빌드는 개발자 PC의 Outlook에 애드인을 자동 등록한다.
#>
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.VisualStudio.Workload.Office -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '"Office/SharePoint 개발" 워크로드가 설치된 Visual Studio를 찾을 수 없습니다.' }
if (-not (Test-Path (Join-Path $repo 'src\TechSupportReply.AddIn\Signing.user.props'))) { & (Join-Path $PSScriptRoot 'New-DevSigningCert.ps1') }
& $msbuild (Join-Path $repo 'src\TechSupportReply.AddIn\TechSupportReply.AddIn.csproj') -restore -nologo -v:m "-p:Configuration=$Configuration"
if ($LASTEXITCODE -ne 0) { throw "빌드 실패(exit $LASTEXITCODE)" }
Write-Host "빌드 완료: $(Join-Path $repo "src\TechSupportReply.AddIn\bin\$Configuration")"
```

- [ ] **Step 5: 빌드와 산출물 확인**

Run: `powershell -ExecutionPolicy Bypass -File tools\New-DevSigningCert.ps1` → 이어서 `powershell -ExecutionPolicy Bypass -File tools\build-addin.ps1 -Configuration Debug`
Expected: "빌드 완료", 경고는 있어도 되고 오류는 0개여야 한다. 확인할 내용:
```powershell
$o = "src\TechSupportReply.AddIn\bin\Debug"
"TechSupportReply.AddIn.vsto","TechSupportReply.AddIn.dll.manifest","TechSupportReply.AddIn.dll.config","onnxruntime.dll","e_sqlite3.dll","TechSupportReply.App.dll" | ForEach-Object { "{0}: {1}" -f $_, (Test-Path "$o\$_") }
Get-ItemProperty "HKCU:\Software\Microsoft\Office\Outlook\Addins\TechSupportReply.AddIn" | Select-Object FriendlyName, LoadBehavior, Manifest
```
모두 True여야 하고, 레지스트리에 `LoadBehavior 3`과 `Manifest=file:///...TechSupportReply.AddIn.vsto|vstolocal`이 있어야 한다.

- [ ] **Step 6: 솔루션에 추가, 테스트 회귀 확인**

Run: `dotnet sln TechSupportReply.sln add src/TechSupportReply.AddIn/TechSupportReply.AddIn.csproj --solution-folder src` (실패하면 VS에서 "기존 프로젝트 추가"를 하거나 sln을 직접 편집한다). 이어서 `dotnet test tests/TechSupportReply.Tests`
Expected: 테스트 전체 PASS(테스트 프로젝트는 AddIn을 참조하지 않는다)

- [ ] **Step 7: 커밋**

```bash
git add .gitignore TechSupportReply.sln src/TechSupportReply.AddIn tools/New-DevSigningCert.ps1 tools/build-addin.ps1
git commit -m "feat(addin): VSTO Outlook 애드인(리본·작업창·메일 추출·회신 초안), 서명·빌드 스크립트"
```

---

### Task 12: 설치(방법 B)·ClickOnce 게시(방법 A) 스크립트

**Files:**
- Create: `tools/install-addin.ps1`, `tools/publish-addin.ps1`
- Modify(필요 시): `src/TechSupportReply.AddIn/TechSupportReply.AddIn.csproj` (ClickOnce에 네이티브 DLL 포함)

**Interfaces:**
- Consumes: Task 11 빌드 산출물
- Produces: `install-addin.ps1 -Source <빌드 폴더> [-Target <설치 폴더>] [-Uninstall]`, `publish-addin.ps1 -PublishDir <폴더> [-InstallUrl <UNC>]`

- [ ] **Step 1: 방법 B 설치 스크립트**

`tools/install-addin.ps1`:
```powershell
<#
.SYNOPSIS
  빌드 폴더를 사용자 폴더로 복사하고 Outlook 애드인으로 등록한다(방법 B, 관리자 권한 불필요).
.EXAMPLE
  .\install-addin.ps1 -Source \\server\deploy\TechSupportReply\bin
  .\install-addin.ps1 -Uninstall
#>
param(
    [string]$Source,
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'Programs\TechSupportReply'),
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$name = 'TechSupportReply.AddIn'
$addinKey = "HKCU:\Software\Microsoft\Office\Outlook\Addins\$name"
$inclusionRoot = 'HKCU:\Software\Microsoft\VSTO\Security\Inclusion'
$manifestUrl = ([Uri](Join-Path $Target "$name.vsto")).AbsoluteUri

if (Get-Process OUTLOOK -ErrorAction SilentlyContinue) { throw 'Outlook을 종료한 뒤 다시 실행하세요.' }

# 이전 신뢰 목록 항목 제거(같은 URL)
if (Test-Path $inclusionRoot) {
    Get-ChildItem $inclusionRoot | Where-Object { (Get-ItemProperty $_.PSPath).Url -eq $manifestUrl } | Remove-Item -Recurse -Force
}

if ($Uninstall) {
    if (Test-Path $addinKey) { Remove-Item $addinKey -Recurse -Force }
    if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
    Write-Host '제거했습니다. 설정(%APPDATA%\TechSupportReply)과 캐시(%LOCALAPPDATA%\TechSupportReply)는 남겨 두었습니다.'
    return
}

if (-not $Source) { throw '-Source(빌드 폴더)를 지정하세요.' }
if (-not (Test-Path (Join-Path $Source "$name.vsto"))) { throw "$Source 에 $name.vsto 가 없습니다." }

New-Item -ItemType Directory -Force $Target | Out-Null
robocopy $Source $Target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "파일 복사 실패(robocopy $LASTEXITCODE)" }

# 서명 인증서 공개 키로 VSTO 신뢰 목록에 추가(설치 확인 창 없이 로드)
[xml]$vsto = Get-Content (Join-Path $Target "$name.vsto") -Raw
$ns = New-Object Xml.XmlNamespaceManager $vsto.NameTable
$ns.AddNamespace('ds', 'http://www.w3.org/2000/09/xmldsig#')
$certNode = $vsto.SelectSingleNode('//ds:X509Certificate', $ns)
if (-not $certNode) { throw '.vsto 매니페스트에서 서명 인증서를 찾을 수 없습니다.' }
$cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (,[Convert]::FromBase64String($certNode.InnerText))
$publicKey = $cert.PublicKey.Key.ToXmlString($false)
$entry = Join-Path $inclusionRoot ([guid]::NewGuid().ToString())
New-Item -Path $entry -Force | Out-Null
New-ItemProperty -Path $entry -Name Url -Value $manifestUrl -PropertyType String -Force | Out-Null
New-ItemProperty -Path $entry -Name PublicKey -Value $publicKey -PropertyType String -Force | Out-Null

New-Item -Path $addinKey -Force | Out-Null
New-ItemProperty -Path $addinKey -Name FriendlyName -Value '기술지원 답변 도우미' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $addinKey -Name Description -Value 'LS-DYNA·Ansys 기술지원 메일 회신 초안(자동 발송 없음)' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $addinKey -Name LoadBehavior -Value 3 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $addinKey -Name Manifest -Value "$manifestUrl|vstolocal" -PropertyType String -Force | Out-Null

Write-Host "설치했습니다: $Target"
Write-Host 'Outlook을 시작하면 [홈] 탭에 "기술지원" 그룹이 나타납니다.'
```

- [ ] **Step 2: 방법 B 검증(개발 PC, Outlook 종료 상태에서만)**

Outlook이 실행 중이면 이 단계는 Task 14로 미룬다. 종료되어 있으면 다음을 실행한다.
```powershell
powershell -ExecutionPolicy Bypass -File tools\build-addin.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File tools\install-addin.ps1 -Source src\TechSupportReply.AddIn\bin\Release
Get-ItemProperty "HKCU:\Software\Microsoft\Office\Outlook\Addins\TechSupportReply.AddIn" | Select-Object Manifest, LoadBehavior
```
Expected: Manifest가 `%LOCALAPPDATA%\Programs\TechSupportReply\TechSupportReply.AddIn.vsto|vstolocal`을 가리킨다. Inclusion 키에 같은 Url이 있다.
(Release 빌드도 bin 경로로 자동 등록되므로, install 스크립트가 같은 키를 설치 폴더 경로로 덮어쓴다.)

- [ ] **Step 3: 방법 A ClickOnce 게시 스크립트**

`tools/publish-addin.ps1`:
```powershell
<#
.SYNOPSIS
  ClickOnce로 애드인을 게시한다(방법 A). 팀원은 게시 폴더의 TechSupportReply.AddIn.vsto를 실행해 설치하고, 이후 Outlook 시작 시 자동 업데이트된다.
.EXAMPLE
  .\publish-addin.ps1 -PublishDir \\server\deploy\TechSupportReply
#>
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [string]$InstallUrl = $PublishDir
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\TechSupportReply.AddIn\TechSupportReply.AddIn.csproj'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.VisualStudio.Workload.Office -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '"Office/SharePoint 개발" 워크로드가 설치된 Visual Studio를 찾을 수 없습니다.' }
if (-not (Test-Path (Join-Path $repo 'src\TechSupportReply.AddIn\Signing.user.props'))) { & (Join-Path $PSScriptRoot 'New-DevSigningCert.ps1') }

# 게시할 때마다 수정 번호를 올린다(ClickOnce 업데이트 판단 기준)
$revFile = Join-Path $repo 'publish\revision.txt'
New-Item -ItemType Directory -Force (Split-Path $revFile) | Out-Null
$rev = if (Test-Path $revFile) { [int](Get-Content $revFile) + 1 } else { 1 }
Set-Content $revFile $rev

& $msbuild $proj -restore -nologo -v:m -t:Publish -p:Configuration=Release "-p:ApplicationRevision=$rev" "-p:InstallUrl=$InstallUrl\" "-p:PublishUrl=$PublishDir\"
if ($LASTEXITCODE -ne 0) { throw "게시 빌드 실패(exit $LASTEXITCODE)" }

$appPublish = Join-Path $repo 'src\TechSupportReply.AddIn\bin\Release\app.publish'
if (-not (Test-Path (Join-Path $appPublish 'TechSupportReply.AddIn.vsto'))) { throw "게시 결과가 없습니다: $appPublish" }
New-Item -ItemType Directory -Force $PublishDir | Out-Null
robocopy $appPublish $PublishDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "게시 폴더 복사 실패(robocopy $LASTEXITCODE)" }
Copy-Item (Join-Path $repo 'publish\TechSupportReply-signing.cer') $PublishDir -ErrorAction SilentlyContinue
Write-Host "게시 완료(수정 $rev): $PublishDir"
```
(스크립트는 Windows PowerShell 5.1에서 실행되므로 `??`, `?.` 같은 PowerShell 7 전용 연산자를 쓰지 않는다.)

- [ ] **Step 4: 게시 검증(로컬 폴더)**

Run: `powershell -ExecutionPolicy Bypass -File tools\publish-addin.ps1 -PublishDir D:\Develop\claude_outlook_addon_autoreply\publish\clickonce`
Expected: `publish\clickonce\TechSupportReply.AddIn.vsto`와 `publish\clickonce\Application Files\TechSupportReply.AddIn_1_0_0_<rev>\`가 생긴다. 네이티브 DLL이 들어갔는지 확인한다.
```powershell
Get-ChildItem -Recurse "publish\clickonce\Application Files" -Include "onnxruntime.dll.deploy","e_sqlite3.dll.deploy" | Select-Object FullName
```
**두 파일이 없으면**(ClickOnce가 NuGet 네이티브 파일을 매니페스트에 넣지 않은 경우) csproj의 `TsrCheckSigning` 대상 아래에 다음을 추가하고 Step 4를 다시 실행한다:
```xml
  <Target Name="TsrIncludeNativeInClickOnce" BeforeTargets="_DeploymentComputeClickOnceManifestInfo">
    <ItemGroup>
      <_TsrNative Include="$(OutDir)onnxruntime.dll;$(OutDir)onnxruntime_providers_shared.dll;$(OutDir)e_sqlite3.dll" Condition="Exists('%(FullPath)')" />
      <ContentWithTargetPath Include="@(_TsrNative)">
        <TargetPath>%(Filename)%(Extension)</TargetPath>
        <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      </ContentWithTargetPath>
    </ItemGroup>
  </Target>
```
`e_sqlite3.dll`이 `$(OutDir)`에 없고 `runtimes\win-x64\native\`에만 있으면 경로를 맞춘다.

- [ ] **Step 5: 커밋**

```bash
git add tools/install-addin.ps1 tools/publish-addin.ps1 src/TechSupportReply.AddIn/TechSupportReply.AddIn.csproj
git commit -m "feat(deploy): 수동 설치(HKCU 등록·신뢰 목록)와 ClickOnce 게시 스크립트"
```

---

### Task 15: API 모델 목록 불러오기 · 작업창 LLM 프로필 선택(2026-09-30 추가)

사용자 요청(2026-09-30): "사용자가 등록된 api 모델을 선택하여 실행하도록 해주세요". 확정한 해석:
1. **설정 대화상자**: 프로필마다 [모델 목록 불러오기] 버튼으로 그 프로필의 키가 쓸 수 있는 모델을 공급자 API에서 받아 드롭다운에 채운다. 모델 칸은 계속 자유 입력이 가능해서 목록에 없는 모델 ID도 쓸 수 있다.
   - Anthropic: `GET https://api.anthropic.com/v1/models`(헤더 `x-api-key`, `anthropic-version: 2023-06-01`, 프로필에 Workspace ID가 있으면 `anthropic-workspace-id`). 최신순으로 오고 페이지 단위다(`has_more`/`last_id`, `limit` 최대 1000). 공식 SDK의 `client.Models.List(new ModelListParams { Limit = 1000 })`와 `page.HasNext()`/`page.Next(ct)`를 쓴다. 이 앱이 실행할 수 없는 모델(`AnthropicProvider`가 거부하는 claude-3·claude-haiku-4-5 등)은 목록에서 뺀다.
   - OpenAI 호환(OpenAI, xAI `https://api.x.ai/v1`): `GET {BaseUrl 또는 https://api.openai.com/v1}/models`, `Authorization: Bearer`. 공식 SDK의 `OpenAIModelClient.GetModelsAsync`를 쓴다. 대화형이 아닌 모델(ID에 embedding·tts·whisper·dall-e·image·moderation·audio·realtime·transcribe·search·davinci·babbage가 들어감)은 빼고 `created` 최신순으로 정렬한다.
   - 두 SDK 모두 `HttpMessageHandler`를 주입해 네트워크 없이 테스트한다(Anthropic은 Task 1의 `AnthropicProvider.CreateHttpClient`, OpenAI는 `HttpClientPipelineTransport`). 오류는 기존 공급자의 `Translate`로 `LlmException`에 매핑한다(401 → Authentication, "not scoped to a workspace" → Task 1의 WorkspaceRequired).
   - SDK 형식·헤더·페이지 넘김·오류 메시지는 2026-09-30에 스크래치 프로젝트(Anthropic 12.50.0, OpenAI 2.14.0, net48)에서 가짜 처리기로 실행해 확인했다.
2. **작업창**: "LLM 프로필" 드롭다운에는 키가 있는 프로필만 "표시명 · 모델"로 보인다("등록된" = DPAPI에 키가 저장됨 또는 `ApiKeyEnvVar` 환경 변수에 값이 있음. Task 1의 `LlmProviderFactory.ResolveApiKey`로 판단). 하나도 없으면 [설정]으로 안내하는 상태 메시지를 보이고 [답변 생성]을 끈다. 사용자가 드롭다운을 바꾸면 `AppSettings.LastProfileId`로 settings.json에 기억한다. 처음 선택은 LastProfileId(키 있음) → DefaultProfileId(키 있음) → 키 있는 첫 프로필 순서다. 분류 프로필 로직은 바꾸지 않는다.

**선행 조건:** 테스트 프로젝트에 `<UseWindowsForms>true</UseWindowsForms>`가 있어야 한다(Task 8·10의 WinForms 테스트도 같은 조건. 없으면 `tests/TechSupportReply.Tests/TechSupportReply.Tests.csproj`의 첫 `PropertyGroup`에 추가한다).

**Files:**
- Modify: `src/TechSupportReply.Core/Settings/AppSettings.cs` (`AppSettings.LastProfileId`)
- Modify: `src/TechSupportReply.Core/Llm/AnthropicProvider.cs` (`IsSupportedModel` 추가, `Translate` private → internal)
- Modify: `src/TechSupportReply.Core/Llm/OpenAiProvider.cs` (`Translate` private → internal)
- Create: `src/TechSupportReply.Core/Llm/LlmModelLister.cs`
- Modify: `src/TechSupportReply.App/Pane/IReplyBackend.cs`, `src/TechSupportReply.App/Pane/IReplyPaneView.cs`, `src/TechSupportReply.App/Pane/ReplyPanePresenter.cs`, `src/TechSupportReply.App/Pane/ReplyTaskPaneControl.cs`
- Modify: `src/TechSupportReply.App/SettingsUi/ISettingsHost.cs`, `src/TechSupportReply.App/SettingsUi/SettingsForm.cs`, `src/TechSupportReply.App/Hosting/AddInServices.cs`
- Modify: `src/TechSupportReply.AddIn/ThisAddIn.cs`, `src/TechSupportReply.AddIn/Outlook/TaskPaneManager.cs`
- Create: `tests/TechSupportReply.Tests/TestSupport/StubHttpHandler.cs`, `tests/TechSupportReply.Tests/Core/Llm/LlmModelListerTests.cs`
- Modify: `tests/TechSupportReply.Tests/TestSupport/FakePaneView.cs`, `tests/TechSupportReply.Tests/TestSupport/FakeBackend.cs`
- Test(수정): `tests/TechSupportReply.Tests/Core/Settings/SettingsStoreTests.cs`, `tests/TechSupportReply.Tests/App/ReplyPanePresenterTests.cs`, `tests/TechSupportReply.Tests/App/ReplyTaskPaneControlTests.cs`, `tests/TechSupportReply.Tests/App/AddInServicesTests.cs`, `tests/TechSupportReply.Tests/App/SettingsFormTests.cs`

**Interfaces:**
- Consumes: Task 1 `LlmProfile.WorkspaceId`, `LlmErrorKind.WorkspaceRequired`, `AnthropicProvider.CreateHttpClient(string workspaceId, HttpMessageHandler handler)`, `Translate`의 워크스페이스 오류 변환, `LlmProviderFactory(SecretStore, Func<string,string>, Func<LlmProfile,string,ILlmProvider>).ResolveApiKey(LlmProfile)`; Task 6 `IReplyBackend`, `ISettingsHost`, `AddInServices`(`Secrets`, `GetEnv`, `SettingsStore`, `_llmFactory`, `_fieldLock`, `_settings`); Task 7 `IReplyPaneView`, `ReplyPanePresenter`, `FakePaneView`, `FakeBackend`; Task 8 `ReplyTaskPaneControl`(`Item`, `_state`, `ProfileCombo`, `GenerateButton`); Task 9 `SettingsEditor.ResolveKey`; Task 10 `SettingsForm`(`_pModel`, `_pTest`, `StoreProfile`, `SelectedProfile`, `_currentProfileId`, `ShowTest`, `AddRow`), `SettingsFormTests.Host`; Task 11 `ThisAddIn.ShowSettings`, `TaskPaneManager`
- Produces:
  - `AppSettings.LastProfileId : string` (기본 "")
  - `AnthropicProvider { public static bool IsSupportedModel(string model); internal static LlmException Translate(Exception ex); }`, `OpenAiProvider { internal static LlmException Translate(Exception ex); }`
  - `sealed class ModelListing { ModelListing(string id, string displayName, DateTimeOffset? createdAt); string Id; string DisplayName; DateTimeOffset? CreatedAt; }`
  - `static class LlmModelLister { static Task<IReadOnlyList<ModelListing>> ListAsync(LlmProfile profile, string apiKey, CancellationToken ct); internal static Task<IReadOnlyList<ModelListing>> ListAsync(LlmProfile, string, HttpMessageHandler handler, CancellationToken); internal static bool IsChatModel(string id); }` — 실패 시 `LlmException`
  - `IReplyBackend`에 추가: `bool HasUsableKey(LlmProfile profile); void SaveLastProfile(string profileId);`
  - `IReplyPaneView`에 추가: `event EventHandler ProfileChangedByUser; void SetGenerateAvailable(bool available);`
  - `ReplyPanePresenter`에 추가: `public const string NoUsableProfileMessage; Task RefreshProfilesAsync(); internal static LlmProfile PickProfile(AppSettings s, IReadOnlyList<LlmProfile> usable);`
  - `ReplyTaskPaneControl`에 추가: `internal static string ProfileLabel(LlmProfile p);` (드롭다운 표시 "표시명 · 모델")
  - `ISettingsHost`에 추가: `Task<IReadOnlyList<ModelListing>> ListModelsAsync(LlmProfile profile, string apiKey, CancellationToken ct);`
  - `SettingsForm`에 추가: `internal ComboBox ModelCombo; internal Label ModelsResultLabel; internal TimeSpan ModelListTimeout {get;set;} = 30초; internal Task LoadModelsAsync();`
  - `TaskPaneManager.RefreshProfiles()`, `ThisAddIn.ShowSettings()`는 저장(OK) 뒤 열린 작업창의 프로필 목록을 다시 채운다.
  - 테스트 지원: `StubHttpHandler { StubHttpHandler Respond(HttpStatusCode, string body); List<RecordedRequest> Requests; }`, `RecordedRequest { string Method; Uri Uri; string Header(string name); }`, `FakePaneView.GenerateAvailable/UserChangesProfile(string)`, `FakeBackend.KeyCheck/SavedLastProfileIds/SaveLastProfileThrows/CreatedProfileIds`

- [ ] **Step 1: Core 실패 테스트 작성**

`tests/TechSupportReply.Tests/TestSupport/StubHttpHandler.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TechSupportReply.Tests.TestSupport
{
    /// <summary>준비한 응답을 차례로 돌려주고 요청(메서드·URI·헤더)을 기록하는 HTTP 처리기. 네트워크 없이 SDK 호출을 검증한다.</summary>
    internal sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly object _lock = new object();
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new Queue<(HttpStatusCode Status, string Body)>();

        public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();

        public StubHttpHandler Respond(HttpStatusCode status, string body)
        {
            lock (_lock) _responses.Enqueue((status, body));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            (HttpStatusCode Status, string Body) next;
            lock (_lock)
            {
                Requests.Add(new RecordedRequest(request));
                if (_responses.Count == 0) throw new InvalidOperationException("준비된 응답이 없습니다: " + request.RequestUri);
                next = _responses.Dequeue();
            }
            return Task.FromResult(new HttpResponseMessage(next.Status)
            {
                Content = new StringContent(next.Body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    /// <summary>SDK가 요청 객체를 해제해도 읽을 수 있도록 보낸 시점에 복사한 요청 정보.</summary>
    internal sealed class RecordedRequest
    {
        public RecordedRequest(HttpRequestMessage request)
        {
            Method = request.Method.Method;
            Uri = request.RequestUri;
            Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        }

        public string Method { get; }
        public Uri Uri { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }

        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }
}
```

`tests/TechSupportReply.Tests/Core/Llm/LlmModelListerTests.cs`:
```csharp
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class LlmModelListerTests
    {
        private static LlmProfile Claude(string workspaceId) =>
            new LlmProfile { DisplayName = "Claude", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", WorkspaceId = workspaceId };

        private static string AnthropicPage(bool hasMore, params string[] ids) =>
            "{\"data\":[" + string.Join(",", ids.Select(id =>
                "{\"type\":\"model\",\"id\":\"" + id + "\",\"display_name\":\"" + id.ToUpperInvariant() + "\",\"created_at\":\"2026-01-01T00:00:00Z\"}"))
            + "],\"has_more\":" + (hasMore ? "true" : "false") + ",\"first_id\":\"" + ids.First() + "\",\"last_id\":\"" + ids.Last() + "\"}";

        private static string OpenAiList(params (string Id, long Created)[] models) =>
            "{\"object\":\"list\",\"data\":[" + string.Join(",", models.Select(m =>
                "{\"id\":\"" + m.Id + "\",\"object\":\"model\",\"created\":" + m.Created + ",\"owned_by\":\"test\"}")) + "]}";

        [Fact]
        public async Task Anthropic_PagesThroughList_SkipsUnsupportedModels_AndSendsHeaders()
        {
            var handler = new StubHttpHandler()
                .Respond(HttpStatusCode.OK, AnthropicPage(true, "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5-20251001"))
                .Respond(HttpStatusCode.OK, AnthropicPage(false, "claude-opus-4-8"));

            var models = await LlmModelLister.ListAsync(Claude(" wrkspc_9 "), "sk-ant-test", handler, CancellationToken.None);

            Assert.Equal(new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-opus-4-8" }, models.Select(m => m.Id));
            Assert.Equal("CLAUDE-OPUS-5-5", models[0].DisplayName);
            Assert.Equal(2, handler.Requests.Count);
            var first = handler.Requests[0];
            Assert.Equal("GET", first.Method);
            Assert.Equal("/v1/models", first.Uri.AbsolutePath);
            Assert.Contains("limit=1000", first.Uri.Query);
            Assert.Equal("sk-ant-test", first.Header("x-api-key"));
            Assert.Equal("2023-06-01", first.Header("anthropic-version"));
            Assert.Equal("wrkspc_9", first.Header("anthropic-workspace-id"));
            Assert.Contains("after_id=claude-haiku-4-5-20251001", handler.Requests[1].Uri.Query);
        }

        [Fact]
        public async Task Anthropic_NoWorkspaceId_HeaderAbsent()
        {
            var handler = new StubHttpHandler().Respond(HttpStatusCode.OK, AnthropicPage(false, "claude-opus-5"));
            var models = await LlmModelLister.ListAsync(Claude(""), "sk-ant-test", handler, CancellationToken.None);
            Assert.Equal(new[] { "claude-opus-5" }, models.Select(m => m.Id));
            Assert.Null(handler.Requests.Single().Header("anthropic-workspace-id"));
        }

        [Fact]
        public async Task Anthropic_Unauthorized_MapsToAuthentication()
        {
            var handler = new StubHttpHandler().Respond(HttpStatusCode.Unauthorized,
                "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}");
            var ex = await Assert.ThrowsAsync<LlmException>(() => LlmModelLister.ListAsync(Claude(""), "sk-bad", handler, CancellationToken.None));
            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
        }

        [Fact]
        public async Task Anthropic_KeyNotScopedToWorkspace_MapsToWorkspaceRequired()
        {
            var handler = new StubHttpHandler().Respond(HttpStatusCode.BadRequest,
                "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"This API key is not scoped to a workspace, so this request must include the anthropic-workspace-id header with the ID of the workspace to use.\"}}");
            var ex = await Assert.ThrowsAsync<LlmException>(() => LlmModelLister.ListAsync(Claude(""), "sk-ant-org", handler, CancellationToken.None));
            Assert.Equal(LlmErrorKind.WorkspaceRequired, ex.Kind);
            Assert.Contains("Workspace ID", ex.UserMessage);
        }

        [Fact]
        public async Task OpenAiCompatible_UsesBaseUrlAndBearer_FiltersNonChat_NewestFirst()
        {
            var handler = new StubHttpHandler().Respond(HttpStatusCode.OK, OpenAiList(
                ("grok-3", 1740000000), ("grok-2-image-1212", 1736000000), ("grok-4", 1752000000), ("text-embedding-3-large", 1760000000)));
            var profile = new LlmProfile { DisplayName = "xAI", Provider = LlmProviderKind.OpenAI, Model = "grok-4", BaseUrl = "https://api.x.ai/v1" };

            var models = await LlmModelLister.ListAsync(profile, "xai-test", handler, CancellationToken.None);

            Assert.Equal(new[] { "grok-4", "grok-3" }, models.Select(m => m.Id));
            var request = handler.Requests.Single();
            Assert.Equal("https://api.x.ai/v1/models", request.Uri.ToString());
            Assert.Equal("Bearer xai-test", request.Header("Authorization"));
        }

        [Fact]
        public async Task OpenAi_DefaultEndpoint_Unauthorized_MapsToAuthentication()
        {
            var handler = new StubHttpHandler().Respond(HttpStatusCode.Unauthorized,
                "{\"error\":{\"message\":\"Incorrect API key provided\",\"type\":\"invalid_request_error\"}}");
            var profile = new LlmProfile { DisplayName = "OpenAI", Provider = LlmProviderKind.OpenAI, Model = "gpt-5.1" };

            var ex = await Assert.ThrowsAsync<LlmException>(() => LlmModelLister.ListAsync(profile, "sk-bad", handler, CancellationToken.None));

            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
            Assert.Equal("https://api.openai.com/v1/models", handler.Requests.Single().Uri.ToString());
        }

        [Theory]
        [InlineData("gpt-5.1", true)]
        [InlineData("grok-4", true)]
        [InlineData("o3", true)]
        [InlineData("text-embedding-3-large", false)]
        [InlineData("tts-1-hd", false)]
        [InlineData("whisper-1", false)]
        [InlineData("dall-e-3", false)]
        [InlineData("gpt-image-1", false)]
        [InlineData("omni-moderation-latest", false)]
        [InlineData("gpt-4o-audio-preview", false)]
        [InlineData("gpt-4o-realtime-preview", false)]
        [InlineData("gpt-4o-transcribe", false)]
        [InlineData("gpt-4o-search-preview", false)]
        [InlineData("davinci-002", false)]
        [InlineData("babbage-002", false)]
        [InlineData("", false)]
        public void IsChatModel(string id, bool expected)
        {
            Assert.Equal(expected, LlmModelLister.IsChatModel(id));
        }
    }
}
```

`tests/TechSupportReply.Tests/Core/Settings/SettingsStoreTests.cs`에 추가:
```csharp
        [Fact]
        public void SaveThenLoad_RoundTripsLastProfileId()
        {
            using (var tmp = new TempDir())
            {
                var store = new SettingsStore(tmp.Root);
                store.Save(new AppSettings { LastProfileId = "p-xai" });
                Assert.Equal("p-xai", store.Load().LastProfileId);
                Assert.Equal("", new AppSettings().LastProfileId);
            }
        }
```

- [ ] **Step 2: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~LlmModelListerTests|FullyQualifiedName~SettingsStoreTests"`
Expected: 컴파일 오류(`LlmModelLister`, `LastProfileId` 없음)

- [ ] **Step 3: Core 구현**

`AppSettings.cs`의 `AppSettings`에서 `ClassifierProfileId` 뒤에 추가:
```csharp
        /// <summary>작업창에서 사용자가 마지막으로 고른 답변 프로필. 키가 없어졌거나 삭제되면 DefaultProfileId로 대체한다.</summary>
        public string LastProfileId { get; set; } = "";
```

`AnthropicProvider.cs`의 `EnsureSupportedModel`을 다음으로 교체한다(메시지는 그대로):
```csharp
        /// <summary>이 앱이 쓸 수 있는 모델인지(적응형 사고를 지원하는 Claude 4.6 이상). 모델 목록 필터에도 쓴다.</summary>
        public static bool IsSupportedModel(string model) =>
            !string.IsNullOrWhiteSpace(model) && !UnsupportedModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        public static void EnsureSupportedModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("모델명이 비어 있습니다.");
            if (!IsSupportedModel(model))
                throw new ArgumentException(
                    $"'{model}'은(는) 지원하지 않습니다. 적응형 사고를 지원하는 Claude 4.6 이상 모델(예: claude-opus-5, claude-sonnet-5)을 사용하세요.");
        }
```
같은 파일의 `private static LlmException Translate(Exception ex)`를 `internal static LlmException Translate(Exception ex)`로 바꾼다(본문과 Task 1의 워크스페이스 분기는 그대로). `OpenAiProvider.cs`의 `private static LlmException Translate(Exception ex)`도 `internal static`으로 바꾼다.

`src/TechSupportReply.Core/Llm/LlmModelLister.cs`:
```csharp
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Models;
using OpenAI;
using OpenAI.Models;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Llm
{
    /// <summary>공급자의 모델 목록 API가 돌려준 모델 하나.</summary>
    public sealed class ModelListing
    {
        public ModelListing(string id, string displayName, DateTimeOffset? createdAt)
        {
            Id = id ?? "";
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName;
            CreatedAt = createdAt;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public DateTimeOffset? CreatedAt { get; }
    }

    /// <summary>
    /// 설정의 [모델 목록 불러오기]. 프로필의 API 키로 쓸 수 있는 모델을 공급자 API에서 받아온다.
    /// Anthropic은 GET /v1/models(페이지 단위, 최신순), OpenAI 호환(OpenAI·xAI)은 GET {BaseUrl}/models를 쓴다.
    /// 네트워크 호출이므로 UI 스레드에서 기다리지 않는다. 실패하면 LlmException을 던진다.
    /// </summary>
    public static class LlmModelLister
    {
        internal const int AnthropicPageLimit = 1000;
        internal const int MaxPages = 10;

        /// <summary>대화형 텍스트 생성에 쓰지 않는 OpenAI 호환 모델 ID에 들어가는 단어.</summary>
        internal static readonly string[] NonChatMarkers =
        {
            "embedding", "tts", "whisper", "dall-e", "image", "moderation", "audio", "realtime", "transcribe", "search", "davinci", "babbage",
        };

        public static Task<IReadOnlyList<ModelListing>> ListAsync(LlmProfile profile, string apiKey, CancellationToken ct) =>
            ListAsync(profile, apiKey, null, ct);

        /// <summary>handler는 테스트용이다. null이면 SDK 기본 전송을 쓴다.</summary>
        internal static Task<IReadOnlyList<ModelListing>> ListAsync(LlmProfile profile, string apiKey, HttpMessageHandler handler, CancellationToken ct)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(apiKey)) throw new LlmException(LlmErrorKind.Authentication, "API 키가 비어 있습니다.");
            switch (profile.Provider)
            {
                case LlmProviderKind.Anthropic: return ListAnthropicAsync(profile, apiKey.Trim(), handler, ct);
                case LlmProviderKind.OpenAI: return ListOpenAiAsync(profile, apiKey.Trim(), handler, ct);
                default: throw new NotSupportedException($"지원하지 않는 공급자: {profile.Provider}");
            }
        }

        internal static bool IsChatModel(string id) =>
            !string.IsNullOrWhiteSpace(id) && !NonChatMarkers.Any(m => id.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);

        private static async Task<IReadOnlyList<ModelListing>> ListAnthropicAsync(LlmProfile profile, string apiKey, HttpMessageHandler handler, CancellationToken ct)
        {
            var result = new List<ModelListing>();
            using (var http = AnthropicProvider.CreateHttpClient((profile.WorkspaceId ?? "").Trim(), handler))
            {
                var client = new AnthropicClient { ApiKey = apiKey, HttpClient = http };
                try
                {
                    ModelListPage page = await client.Models.List(new ModelListParams { Limit = AnthropicPageLimit }, ct).ConfigureAwait(false);
                    for (int pages = 1; ; pages++)
                    {
                        foreach (ModelInfo m in page.Items)
                            if (AnthropicProvider.IsSupportedModel(m.ID)) result.Add(new ModelListing(m.ID, m.DisplayName, m.CreatedAt));
                        if (!page.HasNext() || pages >= MaxPages) break;
                        page = await page.Next(ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (AnthropicProvider.Translate(ex) is LlmException mapped)
                {
                    throw mapped;
                }
            }
            return Distinct(result);
        }

        private static async Task<IReadOnlyList<ModelListing>> ListOpenAiAsync(LlmProfile profile, string apiKey, HttpMessageHandler handler, CancellationToken ct)
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(profile.BaseUrl)) options.Endpoint = new Uri(profile.BaseUrl.Trim());
            var http = handler == null ? null : new HttpClient(handler, disposeHandler: false);
            try
            {
                if (http != null) options.Transport = new HttpClientPipelineTransport(http);
                var client = new OpenAIModelClient(new ApiKeyCredential(apiKey), options);
                ClientResult<OpenAIModelCollection> response = await client.GetModelsAsync(ct).ConfigureAwait(false);
                return Distinct(response.Value
                    .Where(m => IsChatModel(m.Id))
                    .OrderByDescending(m => m.CreatedAt)
                    .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new ModelListing(m.Id, m.Id, m.CreatedAt)));
            }
            catch (Exception ex) when (OpenAiProvider.Translate(ex) is LlmException mapped)
            {
                throw mapped;
            }
            finally
            {
                http?.Dispose();
            }
        }

        private static IReadOnlyList<ModelListing> Distinct(IEnumerable<ModelListing> models)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return models.Where(m => m.Id.Length > 0 && seen.Add(m.Id)).ToList();
        }
    }
}
```

- [ ] **Step 4: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~LlmModelListerTests|FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~AnthropicProviderTests|FullyQualifiedName~AnthropicWorkspaceTests|FullyQualifiedName~SettingsEditorTests"`
Expected: 모두 PASS(`SettingsEditorTests.Validate_ReportsProblems`는 `EnsureSupportedModel` 메시지가 그대로인지 확인한다)

- [ ] **Step 5: App 실패 테스트 작성**

`tests/TechSupportReply.Tests/TestSupport/FakePaneView.cs`에 추가한다(기존 멤버는 그대로):
```csharp
        public event EventHandler ProfileChangedByUser;

        public bool GenerateAvailable { get; private set; } = true;

        public void SetGenerateAvailable(bool available)
        {
            lock (_lock) GenerateAvailable = available;
        }

        public void UserChangesProfile(string id)
        {
            SelectedProfileId = id;
            ProfileChangedByUser?.Invoke(this, EventArgs.Empty);
        }
```

`tests/TechSupportReply.Tests/TestSupport/FakeBackend.cs`: 속성을 추가하고 `CreateLlm`을 교체한다.
```csharp
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
```

`tests/TechSupportReply.Tests/App/ReplyPanePresenterTests.cs`: using 목록에 `using TechSupportReply.Core.Settings;`를 추가하고, 클래스 안에 다음을 추가한다.
```csharp
        private static FakeBackend ThreeProfiles(FakeLlmProvider llm)
        {
            var backend = new FakeBackend(llm);
            backend.Settings.Profiles.Add(new LlmProfile { Id = "p2", DisplayName = "Claude", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5" });
            backend.Settings.Profiles.Add(new LlmProfile { Id = "p3", DisplayName = "xAI", Provider = LlmProviderKind.OpenAI, Model = "grok-4" });
            return backend;
        }

        [Theory]
        [InlineData("p3", "p1", "p3")]
        [InlineData("p2", "p3", "p3")]
        [InlineData("p2", "p2", "p1")]
        [InlineData("", "", "p1")]
        public async Task LoadMail_ListsOnlyProfilesWithKeys_AndSelectsLastThenDefaultThenFirst(string lastId, string defaultId, string expected)
        {
            var view = new FakePaneView();
            var backend = ThreeProfiles(new FakeLlmProvider().Enqueue(DynaJson));
            backend.KeyCheck = profile => profile.Id != "p2";
            backend.Settings.LastProfileId = lastId;
            backend.Settings.DefaultProfileId = defaultId;

            await new ReplyPanePresenter(view, backend).LoadMailAsync(Mail);

            Assert.Equal(new[] { "p1", "p3" }, view.ProfileIds);
            Assert.Equal(expected, view.SelectedProfileId);
            Assert.True(view.GenerateAvailable);
        }

        [Fact]
        public async Task NoProfileWithKey_ShowsSettingsHint_AndGenerateDoesNothing()
        {
            var view = new FakePaneView();
            var llm = new FakeLlmProvider().Enqueue(DynaJson);
            var p = new ReplyPanePresenter(view, new FakeBackend(llm) { KeyCheck = _ => false });
            await p.LoadMailAsync(Mail);

            Assert.Empty(view.ProfileIds);
            Assert.Null(view.SelectedProfileId);
            Assert.False(view.GenerateAvailable);
            Assert.True(view.StatusIsError);
            Assert.Contains("[설정]", view.Status);

            var requests = llm.Requests.Count;
            await p.GenerateAsync();

            Assert.Equal(requests, llm.Requests.Count);
            Assert.Equal(ReplyPanePresenter.NoUsableProfileMessage, view.Status);
            Assert.Equal(PaneState.Idle, view.State);
        }

        [Fact]
        public async Task UserProfileChoice_IsRemembered_AndUsedForGeneration()
        {
            var view = new FakePaneView();
            var backend = ThreeProfiles(new FakeLlmProvider().Enqueue(DynaJson).Enqueue("답"));
            var p = new ReplyPanePresenter(view, backend);
            await p.LoadMailAsync(Mail);

            view.UserChangesProfile("p3");
            await p.GenerateAsync();

            Assert.Equal(new[] { "p3" }, backend.SavedLastProfileIds);
            Assert.Equal("p3", backend.Settings.LastProfileId);
            Assert.Equal("p3", backend.CreatedProfileIds.Last());
            Assert.Equal("답", view.ReplyText);
        }

        [Fact]
        public async Task RememberProfile_Failure_IsLoggedNotThrown()
        {
            var view = new FakePaneView();
            var backend = new FakeBackend(new FakeLlmProvider().Enqueue(DynaJson)) { SaveLastProfileThrows = new System.IO.IOException("잠김") };
            await new ReplyPanePresenter(view, backend).LoadMailAsync(Mail);

            Assert.Null(Record.Exception(() => view.UserChangesProfile("p1")));
        }

        [Fact]
        public async Task RefreshProfiles_AfterKeyAdded_EnablesGenerate()
        {
            var view = new FakePaneView();
            var backend = new FakeBackend(new FakeLlmProvider().Enqueue(DynaJson)) { KeyCheck = _ => false };
            var p = new ReplyPanePresenter(view, backend);
            await p.LoadMailAsync(Mail);
            Assert.False(view.GenerateAvailable);

            backend.KeyCheck = null;
            await p.RefreshProfilesAsync();

            Assert.True(view.GenerateAvailable);
            Assert.Equal(new[] { "p1" }, view.ProfileIds);
            Assert.Equal("p1", view.SelectedProfileId);
        }
```

`tests/TechSupportReply.Tests/App/ReplyTaskPaneControlTests.cs`에 추가:
```csharp
        [Fact]
        public void Profiles_ShowNameAndModel_ProgrammaticSelectionRaisesNoUserEvent()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    int userChanges = 0;
                    c.ProfileChangedByUser += (s, e) => userChanges++;
                    c.SetProfiles(new[]
                    {
                        new LlmProfile { Id = "a", DisplayName = "Claude", Model = "claude-opus-5" },
                        new LlmProfile { Id = "b", DisplayName = "xAI Grok", Model = "grok-4" },
                    }, "b");

                    Assert.Equal(new[] { "Claude · claude-opus-5", "xAI Grok · grok-4" }, c.ProfileCombo.Items.Cast<object>().Select(o => o.ToString()));
                    Assert.Equal("b", c.SelectedProfileId);
                    Assert.Equal(0, userChanges);
                }
            });
        }

        [Fact]
        public void GenerateUnavailable_KeepsGenerateDisabledWhileIdle()
        {
            Sta.Run(() =>
            {
                using (var c = new ReplyTaskPaneControl())
                {
                    c.SetGenerateAvailable(false);
                    c.SetState(PaneState.Idle);
                    Assert.False(c.GenerateButton.Enabled);
                    Assert.True(c.DraftButton.Enabled);

                    c.SetGenerateAvailable(true);
                    Assert.True(c.GenerateButton.Enabled);

                    c.SetState(PaneState.Generating);
                    c.SetGenerateAvailable(true);
                    Assert.False(c.GenerateButton.Enabled);
                }
            });
        }
```
(기존 `Profiles_SelectedId`는 모델이 빈 프로필이라 표시가 "A", "B" 그대로이므로 고치지 않는다.)

`tests/TechSupportReply.Tests/App/AddInServicesTests.cs`에 추가:
```csharp
        [Fact]
        public void HasUsableKey_EnvValueOrStoredSecret()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), n => n == "OPENAI_API_KEY" ? "sk" : null);
                services.Secrets.Set("s1", "stored");

                Assert.True(services.HasUsableKey(services.Settings.Profiles.Single()));
                Assert.True(services.HasUsableKey(new LlmProfile { SecretId = "s1" }));
                Assert.False(services.HasUsableKey(new LlmProfile { ApiKeyEnvVar = "XAI_API_KEY" }));
                Assert.False(services.HasUsableKey(new LlmProfile { SecretId = "missing" }));
                Assert.False(services.HasUsableKey(null));
            }
        }

        [Fact]
        public void SaveLastProfile_Persists_WithoutRebuildingSession()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                var session = services.GetSession();

                services.SaveLastProfile("p-xai");

                Assert.Same(session, services.GetSession());
                Assert.Equal("p-xai", services.Settings.LastProfileId);
                Assert.Equal("p-xai", new SettingsStore(Path.Combine(tmp.Root, "settings")).Load().LastProfileId);
            }
        }
```

`tests/TechSupportReply.Tests/App/SettingsFormTests.cs`: using 목록에 `using System.Diagnostics;`와 `using System.Windows.Forms;`를 추가한다. `Host`의 `GetEnv` 줄을 다음으로 바꾸고 멤버를 추가한다.
```csharp
            public Func<string, string> GetEnv { get; set; } = _ => null;
            public Func<LlmProfile, string, CancellationToken, Task<IReadOnlyList<ModelListing>>> ListModels { get; set; } =
                (p, k, ct) => Task.FromResult<IReadOnlyList<ModelListing>>(new ModelListing[0]);
            public List<(LlmProfile Profile, string Key)> ListCalls { get; } = new List<(LlmProfile Profile, string Key)>();

            public Task<IReadOnlyList<ModelListing>> ListModelsAsync(LlmProfile profile, string apiKey, CancellationToken ct)
            {
                lock (ListCalls) ListCalls.Add((profile, apiKey));
                return ListModels(profile, apiKey, ct);
            }
```
클래스 안에 도우미와 테스트를 추가한다.
```csharp
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
```

- [ ] **Step 6: 실패 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~ReplyPanePresenterTests|FullyQualifiedName~ReplyTaskPaneControlTests|FullyQualifiedName~AddInServicesTests|FullyQualifiedName~SettingsFormTests"`
Expected: 컴파일 오류(`IReplyBackend.HasUsableKey`, `IReplyPaneView.ProfileChangedByUser`, `ISettingsHost.ListModelsAsync`, `SettingsForm.LoadModelsAsync` 등 없음)

- [ ] **Step 7: App 구현**

`src/TechSupportReply.App/Pane/IReplyBackend.cs`의 인터페이스에 추가:
```csharp
        /// <summary>
        /// 프로필에 쓸 수 있는 API 키가 있는지(DPAPI 저장 키 또는 값이 있는 ApiKeyEnvVar). 로컬 파일과 레지스트리를 읽으므로
        /// 프레젠터는 백그라운드에서 호출한다.
        /// </summary>
        bool HasUsableKey(LlmProfile profile);
        /// <summary>작업창에서 사용자가 고른 답변 프로필을 settings.json(LastProfileId)에 기억한다.</summary>
        void SaveLastProfile(string profileId);
```

`src/TechSupportReply.App/Pane/IReplyPaneView.cs`: `event EventHandler ProductChangedByUser;` 뒤에 이벤트를, `SetProfiles` 뒤에 메서드를 추가하고 `SetProfiles`에 주석을 단다.
```csharp
        /// <summary>사용자가 LLM 프로필 드롭다운을 직접 바꿨을 때만 발생한다(코드로 선택할 때는 발생하지 않음).</summary>
        event EventHandler ProfileChangedByUser;
```
```csharp
        /// <summary>키가 있는 프로필만 받는다. 화면에는 "표시명 · 모델"로 보인다.</summary>
        void SetProfiles(IReadOnlyList<LlmProfile> profiles, string selectedId);
        /// <summary>false면 상태와 관계없이 [답변 생성]을 끈다(키가 있는 프로필이 없을 때).</summary>
        void SetGenerateAvailable(bool available);
```

`src/TechSupportReply.App/Hosting/AddInServices.cs`의 `CreateLlmForTest` 뒤에 추가:
```csharp
        public bool HasUsableKey(LlmProfile profile)
        {
            if (profile == null) return false;
            try
            {
                return new LlmProviderFactory(Secrets, GetEnv, _llmFactory).ResolveApiKey(profile) != null;
            }
            catch (InvalidOperationException ex)
            {
                // secrets.dat을 복호화할 수 없으면(다른 사용자 계정·손상) 키가 없는 것으로 본다.
                Log.Warn($"'{profile.DisplayName}' 프로필 키 확인 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>LastProfileId만 바꿔 저장한다. 지식 세션은 이 값에 의존하지 않으므로 다시 만들지 않는다.</summary>
        public void SaveLastProfile(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return;
            lock (_fieldLock)
            {
                if (_settings.LastProfileId == profileId) return;
                _settings.LastProfileId = profileId;
                SettingsStore.Save(_settings);
            }
        }

        public Task<IReadOnlyList<ModelListing>> ListModelsAsync(LlmProfile profile, string apiKey, CancellationToken ct) =>
            LlmModelLister.ListAsync(profile, apiKey, ct);
```

`src/TechSupportReply.App/SettingsUi/ISettingsHost.cs`: `using System.Collections.Generic;`을 추가하고 인터페이스 끝에 추가:
```csharp
        /// <summary>[모델 목록 불러오기]. 네트워크 호출이므로 UI 스레드에서 기다리지 않는다. 실패하면 LlmException.</summary>
        Task<IReadOnlyList<ModelListing>> ListModelsAsync(LlmProfile profile, string apiKey, CancellationToken ct);
```

`src/TechSupportReply.App/Pane/ReplyPanePresenter.cs`:
1. 필드 `private bool _userChoseProduct;` 뒤에 추가:
```csharp
        private bool _hasUsableProfile;

        public const string NoUsableProfileMessage =
            "API 키가 등록된 LLM 프로필이 없습니다. [설정] → LLM 프로필에서 키를 입력하거나 환경 변수를 설정한 뒤 다시 시도하세요.";
```
2. 생성자의 `_view.ProductChangedByUser += ...;` 뒤에 추가:
```csharp
            _view.ProfileChangedByUser += (s, e) => RememberProfile();
```
3. `LoadMailAsync`에서 다음 부분을
```csharp
            var settings = _backend.Settings;
            _view.SetProfiles(settings.Profiles, ResolveProfile(settings, settings.DefaultProfileId)?.Id);
            try
            {
                var session = await Task.Run(() => _backend.GetSession(), cts.Token);
```
다음으로 바꾼다.
```csharp
            var settings = _backend.Settings;
            try
            {
                await RefreshProfilesAsync();
                if (version != _mailVersion) return;
                var session = await Task.Run(() => _backend.GetSession(), cts.Token);
```
같은 메서드의 상태 메시지 두 줄
```csharp
                if (llmProblem != null) _view.SetStatus("키워드로 제품군을 판별했습니다. " + llmProblem, true);
                else _view.SetStatus("제품군을 확인하거나 바꾼 뒤 [답변 생성]을 누르세요.", false);
```
을 다음으로 바꾼다.
```csharp
                if (!_hasUsableProfile) _view.SetStatus(NoUsableProfileMessage, true);
                else if (llmProblem != null) _view.SetStatus("키워드로 제품군을 판별했습니다. " + llmProblem, true);
                else _view.SetStatus("제품군을 확인하거나 바꾼 뒤 [답변 생성]을 누르세요.", false);
```
4. `GenerateAsync`에서 `_mail == null` 검사 블록 바로 뒤에 추가:
```csharp
            if (!_hasUsableProfile)
            {
                _view.SetStatus(NoUsableProfileMessage, true);
                return;
            }
```
5. `Stop()` 앞에 추가:
```csharp
        /// <summary>
        /// 키가 있는 프로필만 드롭다운에 다시 채우고 [답변 생성] 사용 가능 여부를 정한다. 메일을 불러올 때와 설정을 저장한 뒤 호출한다.
        /// 키 확인(secrets.dat·레지스트리)은 백그라운드에서 한다. 예외를 던지지 않는다.
        /// </summary>
        public async Task RefreshProfilesAsync()
        {
            var settings = _backend.Settings;
            var usable = await Task.Run(() => settings.Profiles.Where(IsUsable).ToList());
            _hasUsableProfile = usable.Count > 0;
            _view.SetProfiles(usable, PickProfile(settings, usable)?.Id);
            _view.SetGenerateAvailable(_hasUsableProfile);
        }

        /// <summary>처음 선택: 마지막 선택(키 있음) → 기본 답변 프로필(키 있음) → 키 있는 첫 프로필.</summary>
        internal static LlmProfile PickProfile(AppSettings s, IReadOnlyList<LlmProfile> usable) =>
            usable.FirstOrDefault(p => p.Id == s.LastProfileId)
            ?? usable.FirstOrDefault(p => p.Id == s.DefaultProfileId)
            ?? usable.FirstOrDefault();

        private bool IsUsable(LlmProfile profile)
        {
            try
            {
                return _backend.HasUsableKey(profile);
            }
            catch (Exception ex)
            {
                _backend.Log.Warn($"'{profile.DisplayName}' 프로필 키 확인 실패: {ex.Message}");
                return false;
            }
        }

        private void RememberProfile()
        {
            var id = _view.SelectedProfileId;
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                _backend.SaveLastProfile(id);
            }
            catch (Exception ex)
            {
                _backend.Log.Warn("마지막으로 고른 프로필을 저장하지 못했습니다: " + ex.Message);
            }
        }
```
(`ResolveProfile`은 분류 프로필에만 계속 쓴다.)

`src/TechSupportReply.App/Pane/ReplyTaskPaneControl.cs`:
1. 필드 `private PaneState _state = PaneState.Idle;` 뒤에 `private bool _generateAvailable = true;`를 추가한다.
2. 생성자의 `ProductCombo.SelectionChangeCommitted += ...;` 뒤에 추가:
```csharp
            ProfileCombo.SelectionChangeCommitted += (s, e) => ProfileChangedByUser?.Invoke(this, EventArgs.Empty);
```
3. 이벤트 목록에 `public event EventHandler ProfileChangedByUser;`를 추가한다.
4. `SetProfiles`의 `foreach` 줄을 `foreach (var p in profiles) ProfileCombo.Items.Add(new Item(p.Id, ProfileLabel(p)));`로 바꾼다.
5. `SetState`의 `GenerateButton.Enabled = idle;`를 `GenerateButton.Enabled = idle && _generateAvailable;`로 바꾼다.
6. `SetState` 뒤에 추가:
```csharp
        public void SetGenerateAvailable(bool available)
        {
            _generateAvailable = available;
            GenerateButton.Enabled = _state == PaneState.Idle && available;
        }

        /// <summary>드롭다운 표시: "표시명 · 모델"(모델이 비어 있으면 표시명만).</summary>
        internal static string ProfileLabel(LlmProfile p) =>
            string.IsNullOrWhiteSpace(p.Model) ? p.DisplayName : p.DisplayName + " · " + p.Model.Trim();
```

`src/TechSupportReply.App/SettingsUi/SettingsForm.cs`:
1. using 목록에 `using System.Threading.Tasks;`를 추가한다.
2. 필드 `private readonly TextBox _pModel = new TextBox { Dock = DockStyle.Fill };`를 편집 가능한 드롭다운으로 바꾸고, 그 아래에 필드를 추가한다(`LoadProfile`의 `_pModel.Text = p.Model;`과 `StoreProfileById`의 `p.Model = _pModel.Text.Trim();`은 그대로 동작한다).
```csharp
        private readonly ComboBox _pModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
        private readonly Button _pLoadModels = new Button { Text = "모델 목록 불러오기", AutoSize = true };
        private readonly Label _pModelsResult = new Label { AutoSize = true, MaximumSize = new Size(420, 0) };
        private const string NoKeyMessage = "API 키가 없습니다. 키를 입력하거나 환경 변수를 확인하세요.";
```
3. `internal SettingsEditor Editor { get; }` 뒤에 추가:
```csharp
        internal ComboBox ModelCombo => _pModel;
        internal Label ModelsResultLabel => _pModelsResult;
        internal TimeSpan ModelListTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// [모델 목록 불러오기]. 입력 중인 값을 반영한 프로필 사본과 키로 호스트에 목록을 요청하고(백그라운드), 결과를 모델 드롭다운에 채운다.
        /// 모델 칸의 현재 값은 지우지 않는다(목록에 없는 모델도 직접 입력해 쓸 수 있다). 예외를 던지지 않는다.
        /// </summary>
        internal async Task LoadModelsAsync()
        {
            _pLoadModels.Enabled = false;
            try
            {
                var p = CurrentProfileWithKey(out var key);
                if (p == null) return;
                if (string.IsNullOrWhiteSpace(key)) { ShowResult(_pModelsResult, false, NoKeyMessage); return; }
                var snapshot = new LlmProfile
                {
                    Id = p.Id, DisplayName = p.DisplayName, Provider = p.Provider, Model = p.Model, BaseUrl = p.BaseUrl, WorkspaceId = p.WorkspaceId,
                };
                ShowResult(_pModelsResult, true, "모델 목록을 불러오는 중…");
                IReadOnlyList<ModelListing> models;
                using (var cts = new CancellationTokenSource(ModelListTimeout))
                    models = await Task.Run(() => _host.ListModelsAsync(snapshot, key, cts.Token), cts.Token);
                if (IsDisposed || _currentProfileId != snapshot.Id) return;   // 그사이 창을 닫았거나 다른 프로필을 골랐다
                FillModels(models);
            }
            catch (OperationCanceledException)
            {
                ShowResult(_pModelsResult, false, "응답 시간이 초과되었습니다. 네트워크 또는 Base URL을 확인하세요.");
            }
            catch (LlmException ex)
            {
                ShowResult(_pModelsResult, false, ex.UserMessage);
            }
            catch (Exception ex)
            {
                ShowResult(_pModelsResult, false, "모델 목록을 불러오지 못했습니다: " + ex.Message);
            }
            finally
            {
                _pLoadModels.Enabled = true;
            }
        }
```
4. `BuildProfilesTab`의 `AddRow(editor, "모델", _pModel);`를 다음 두 줄로 바꾼다.
```csharp
            AddRow(editor, "모델", InlineRow(_pModel, _pLoadModels));
            AddRow(editor, "", _pModelsResult);
```
같은 메서드의 `_pTest.Click` 처리기에서 앞부분
```csharp
                StoreProfile();
                var p = SelectedProfile();
                if (p == null) return;
                var key = Editor.ResolveKey(p, _host.GetEnv);
                if (string.IsNullOrWhiteSpace(key)) { ShowTest(false, "API 키가 없습니다. 키를 입력하거나 환경 변수를 확인하세요."); return; }
```
을 다음으로 바꾼다([모델 목록 불러오기]와 같은 키 해석을 공유한다).
```csharp
                var p = CurrentProfileWithKey(out var key);
                if (p == null) return;
                if (string.IsNullOrWhiteSpace(key)) { ShowTest(false, NoKeyMessage); return; }
```
`return page;` 바로 앞에 `_pLoadModels.Click += async (s, e) => await LoadModelsAsync();`를 추가한다.
5. `LoadProfile`의 `_pTestResult.Text = "";` 뒤에 추가(프로필을 바꾸면 이전 목록을 지운다):
```csharp
            _pModel.Items.Clear();
            _pModelsResult.Text = "";
```
6. `ShowTest` 본문을 `ShowResult(_pTestResult, ok, message);`로 바꾸고, 그 뒤에 추가한다.
```csharp
        private static void ShowResult(Label label, bool ok, string message)
        {
            label.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
            label.Text = message;
        }

        /// <summary>편집 중인 값을 반영한 뒤 선택한 프로필과 [연결 테스트]·[모델 목록 불러오기]에 쓸 키(환경 변수 → 입력 중 → 저장됨)를 돌려준다.</summary>
        private LlmProfile CurrentProfileWithKey(out string key)
        {
            StoreProfile();
            var p = SelectedProfile();
            key = p == null ? null : Editor.ResolveKey(p, _host.GetEnv);
            return p;
        }

        private void FillModels(IReadOnlyList<ModelListing> models)
        {
            var current = _pModel.Text.Trim();
            _pModel.BeginUpdate();
            _pModel.Items.Clear();
            foreach (var m in models) _pModel.Items.Add(m.Id);
            _pModel.EndUpdate();
            _pModel.Text = current;
            if (models.Count == 0)
            {
                ShowResult(_pModelsResult, false, "이 키로 쓸 수 있는 모델이 없습니다. 키 권한과 Base URL을 확인하세요.");
                return;
            }
            var note = models.Any(m => string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase))
                ? ""
                : " 현재 모델은 목록에 없습니다(직접 입력한 값도 그대로 쓸 수 있습니다).";
            ShowResult(_pModelsResult, true, $"모델 {models.Count}개를 불러왔습니다. 목록에서 고르거나 직접 입력하세요.{note}");
        }
```
7. `BuildKnowledgeTab`의 `rootRow` 생성 다섯 줄(`var rootRow = ...`부터 `rootRow.Controls.Add(browse, 1, 0);`까지)을 지우고 `AddRow(t, "RAG 루트", rootRow);`를 `AddRow(t, "RAG 루트", InlineRow(_ragRoot, browse));`로 바꾼다. 배치 도우미 영역에 추가한다(모델 줄과 같은 배치를 공유한다).
```csharp
        /// <summary>입력 칸(남는 폭 전부)과 옆 버튼을 한 줄에 놓는다.</summary>
        private static TableLayoutPanel InlineRow(Control main, Control side)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(main, 0, 0);
            row.Controls.Add(side, 1, 0);
            return row;
        }
```
(모델 드롭다운의 항목은 모델 ID 문자열이다. 항목을 고르면 드롭다운 텍스트가 곧 모델 ID가 되고, `StoreProfileById`의 `p.Model = _pModel.Text.Trim();`이 그대로 저장한다.)

- [ ] **Step 8: 통과 확인**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~ReplyPanePresenterTests|FullyQualifiedName~ReplyTaskPaneControlTests|FullyQualifiedName~AddInServicesTests|FullyQualifiedName~SettingsFormTests"`
Expected: PASS(기존 테스트 포함). 이어서 `dotnet test tests/TechSupportReply.Tests`로 전체 회귀가 없는지 확인한다.

- [ ] **Step 9: 애드인에서 설정 저장 후 작업창 갱신, 빌드**

`src/TechSupportReply.AddIn/ThisAddIn.cs`의 `ShowSettings`를 교체한다.
```csharp
        internal void ShowSettings()
        {
            using (var form = new SettingsForm(Services))
            {
                // 키·프로필을 바꿨을 수 있으므로 저장했으면 열린 작업창의 프로필 목록을 다시 채운다.
                if (form.ShowDialog() == DialogResult.OK) _panes?.RefreshProfiles();
            }
        }
```
`src/TechSupportReply.AddIn/Outlook/TaskPaneManager.cs`의 `Dispose` 앞에 추가:
```csharp
        /// <summary>설정을 저장한 뒤 호출한다. RefreshProfilesAsync는 예외를 던지지 않는다.</summary>
        public void RefreshProfiles()
        {
            foreach (var entry in _panes.Values.ToList()) _ = entry.Presenter.RefreshProfilesAsync();
        }
```
Run: `powershell -ExecutionPolicy Bypass -File tools\build-addin.ps1 -Configuration Debug`
Expected: "빌드 완료", 오류 0개

- [ ] **Step 10: 커밋**

```bash
git add src/TechSupportReply.Core src/TechSupportReply.App src/TechSupportReply.AddIn/ThisAddIn.cs src/TechSupportReply.AddIn/Outlook/TaskPaneManager.cs tests/TechSupportReply.Tests
git commit -m "feat: API 모델 목록 불러오기와 작업창 LLM 프로필 선택(키 있는 프로필만, 마지막 선택 기억)"
```

---

### Task 13: 한국어 문서 3종

**Files:**
- Create: `docs/설치가이드.md`, `docs/관리자가이드.md`, `docs/사용자가이드.md`

- [ ] **Step 1: `docs/설치가이드.md` 작성** — 다음 목차를 채운다. 명령은 Task 11~12의 스크립트와 정확히 같아야 한다.
  1. 사전 요구사항: Outlook Classic x64 확인 방법(파일 → Office 계정 → Outlook 정보 → "64비트"), .NET Framework 4.8, VSTO Runtime(Office 2016+에 포함, 확인할 레지스트리 `HKLM\SOFTWARE\WOW6432Node\Microsoft\VSTO Runtime Setup\v4R`)
  2. 방법 A(권장, 팀 배포, ClickOnce): 관리자가 `tools\publish-addin.ps1 -PublishDir \\server\deploy\TechSupportReply`를 실행한다. 팀원은 Outlook을 종료하고 `\\server\deploy\TechSupportReply\TechSupportReply.AddIn.vsto`를 더블클릭해 [설치]한다. 이후 Outlook 시작 시 자동으로 업데이트된다. 자체 서명 인증서라서 뜨는 "게시자를 확인할 수 없습니다" 경고와 이를 없애는 방법(`TechSupportReply-signing.cer`를 "신뢰할 수 있는 게시자"와 "신뢰할 수 있는 루트 인증 기관"에 배포하거나, 회사 코드 서명 인증서를 `New-DevSigningCert.ps1 -PfxPath`로 지정)을 설명한다. 공유 폴더 설치는 "로컬 인트라넷" 영역이어야 한다는 점도 적는다.
  3. 방법 B(수동 등록): `tools\install-addin.ps1 -Source <빌드 폴더>`. 스크립트가 하는 일(복사, HKCU Addins 키, VSTO Inclusion 신뢰 목록)을 설명하고 동등한 `.reg` 예시를 넣는다:
     ```
     Windows Registry Editor Version 5.00

     [HKEY_CURRENT_USER\Software\Microsoft\Office\Outlook\Addins\TechSupportReply.AddIn]
     "FriendlyName"="기술지원 답변 도우미"
     "Description"="LS-DYNA·Ansys 기술지원 메일 회신 초안(자동 발송 없음)"
     "LoadBehavior"=dword:00000003
     "Manifest"="file:///C:/Users/<사용자>/AppData/Local/Programs/TechSupportReply/TechSupportReply.AddIn.vsto|vstolocal"
     ```
  4. 방법 C(개발자): VS 2022에서 `TechSupportReply.sln`을 열고 `TechSupportReply.AddIn`을 시작 프로젝트로 정한 뒤 F5를 누른다. 또는 `tools\build-addin.ps1`을 실행하면 빌드할 때 자동으로 등록된다. `dotnet build`로는 VSTO 프로젝트를 빌드할 수 없다.
  5. Outlook에서 활성화 확인: 파일 → 옵션 → 추가 기능 → 관리: COM 추가 기능 [이동] → "기술지원 답변 도우미" 체크
  6. 문제 해결: `LoadBehavior`가 2로 바뀐 경우(로드 실패 → 3으로 되돌리고 원인 확인), "사용 안 함 항목" 복구(파일 → 옵션 → 추가 기능 → 관리: 사용할 수 없는 항목), 느린 애드인 자동 비활성화 방지(`HKCU\Software\Microsoft\Office\16.0\Outlook\Resiliency\DoNotDisableAddinList`에 `TechSupportReply.AddIn`=DWORD 1 추가, 또는 GPO "관리되는 추가 기능 목록"), 로드 오류 표시(시스템 환경 변수 `VSTO_SUPPRESSDISPLAYALERTS=0`), 로그 위치 `%LOCALAPPDATA%\TechSupportReply\logs`, 환경 변수 키를 추가했으면 Outlook을 다시 시작한다는 점
  7. 제거: 방법 A는 제어판 → 프로그램 제거 → "기술지원 답변 도우미", 방법 B는 `tools\install-addin.ps1 -Uninstall`, 개발 PC는 VS에서 [솔루션 정리]를 하거나 레지스트리 키를 삭제한다.

- [ ] **Step 2: `docs/관리자가이드.md` 작성** — 공유 폴더 구조(명세 §2 그대로), `products.json` 예시와 필드 설명(`tools`의 `init-kb` 명령으로 생성: `TechSupportReply.Indexer.exe init-kb --root \\server\KB`), 제품별 `_prompt.md` 작성 요령(어조·금지사항·자주 요청할 파일), ONNX 모델 배치(`_models\bge-m3-int8\model.onnx`, `sentencepiece.bpe.model`, `tools/download_model.sh`), 색인 실행(`TechSupportReply.Indexer.exe index --root \\server\KB [--product ls-dyna] [--full]`)과 작업 스케줄러 등록 예시(`schtasks /Create /TN "TechSupportReply Index" /SC DAILY /ST 02:00 /TR "\"C:\Tools\TechSupportReply.Indexer\TechSupportReply.Indexer.exe\" index --root \\server\KB"`), 게시(`publish-addin.ps1`)와 버전 올리기, 인증서 관리. Indexer의 실제 옵션은 `src/TechSupportReply.Indexer/Program.cs`의 도움말을 읽어 그대로 옮긴다.

- [ ] **Step 3: `docs/사용자가이드.md` 작성** — 첫 실행(환경 변수 `ANTHROPIC_API_KEY`/`OPENAI_API_KEY`/`XAI_API_KEY`가 있으면 프로필이 자동으로 만들어짐, 워크스페이스에 속하지 않은 Anthropic 키는 Workspace ID 필요), [설정]에서 프로필 추가·키 입력·[연결 테스트]·RAG 루트 지정, 모델 고르기(Task 15: 프로필의 [모델 목록 불러오기]를 누르면 그 키로 쓸 수 있는 모델이 드롭다운에 채워진다. Claude는 적응형 사고를 지원하는 모델만, OpenAI·xAI는 대화형 모델만 최신순으로 보인다. 목록에 없는 모델 ID도 직접 입력할 수 있다. 워크스페이스 오류가 나면 Workspace ID를 입력한다. 고른 뒤 [저장]), 작업창의 "LLM 프로필" 드롭다운(키가 있는 프로필만 "표시명 · 모델"로 보인다. 마지막으로 고른 프로필을 기억한다. 키가 있는 프로필이 없으면 [답변 생성]이 꺼지고 [설정]으로 안내한다. 설정을 저장하면 열린 작업창 목록이 바로 갱신된다), 사용 흐름(메일 선택 → [기술지원 답변] → 제품군 확인·변경 → 필요하면 LLM 프로필 선택·추가 지시 → [답변 생성] → 검토·수정 → [회신 초안 만들기] → 초안 창에서 최종 검토 후 직접 [보내기]), [중지], 참고 문서·경고 읽는 법, `[확인 필요]` 표시의 의미, 개인정보 주의(고객 메일 본문과 텍스트 첨부 발췌가 선택한 LLM 공급자에게 전송됨. 지식 문서는 로컬에서 임베딩되며 검색된 발췌만 전송됨), 자주 묻는 질문(키 오류, 워크스페이스 오류, 공유 폴더 오프라인)

- [ ] **Step 4: 커밋**

```bash
git add docs/설치가이드.md docs/관리자가이드.md docs/사용자가이드.md
git commit -m "docs: 설치·관리자·사용자 가이드"
```

---

### Task 14: Outlook E2E 검증(수동, 사용자와 함께)

Outlook 재시작이 필요하므로 사용자 확인을 받은 뒤 진행한다.

- [ ] **Step 1: 등록 확인 후 Outlook 재시작** — `tools\build-addin.ps1`(Debug)을 실행한 뒤 Outlook을 다시 시작한다. [홈] 탭에 "기술지원" 그룹이 있는지 확인한다. 없으면 `LoadBehavior` 값, "사용할 수 없는 항목" 목록, `VSTO_SUPPRESSDISPLAYALERTS=0` 설정 후 표시되는 오류 창을 확인한다.
- [ ] **Step 2: 네이티브 DLL 확인** — 답변 1회 생성 후(RAG 루트 설정 시) Process Explorer 또는 `Get-Process OUTLOOK | % { $_.Modules } | ? ModuleName -in 'onnxruntime.dll','e_sqlite3.dll' | select FileName`으로 애드인 폴더 경로에서 로드되었는지 확인한다(System32 경로면 실패).
- [ ] **Step 3: 명세 §7 E2E 시나리오** — 샘플 KB(`samples/kb`)를 Indexer로 색인하고 RAG 루트로 지정한 뒤 다음을 확인한다. (1) LS-DYNA 메일 → 제품군 LS-DYNA, Fluent로 바꾸면 참고 문서가 바뀐다. (2) 스트리밍 표시, [중지] 동작. (3) [회신 초안 만들기] → 서명과 인용이 보존되고 답변이 맨 위에 온다. 일반 텍스트 메일에서도 확인한다. (4) 프로필 Claude ↔ OpenAI ↔ xAI로 바꿔 다시 생성한다. 잘못된 키로 [연결 테스트]를 하면 오류 메시지가 나온다. (5) RAG 루트를 없는 경로로 바꾸면 경고가 표시되고 RAG 없이 생성된다. (6) 읽기 창(메일 더블클릭)에서도 리본 버튼이 동작한다. (7) 모델 선택(Task 15): [설정] → LLM 프로필에서 Claude·OpenAI·xAI 프로필마다 [모델 목록 불러오기]를 눌러 목록이 채워지는지 확인한다(Claude 목록에 claude-haiku-4-5 같은 미지원 모델이 없고, OpenAI 목록에 embedding·tts·whisper·dall-e 모델이 없다. Workspace ID를 비운 조직 키는 Workspace ID 안내가 나온다. 잘못된 키는 키 오류가 나온다). 목록에서 다른 모델을 고르고 [저장]하면 열린 작업창 드롭다운이 "표시명 · 새 모델"로 바로 바뀐다. 작업창에서 프로필을 바꿔 [답변 생성]하면 그 프로필로 생성되고, Outlook을 다시 시작해도 마지막으로 고른 프로필이 선택되어 있다(settings.json의 lastProfileId). 키가 없는 프로필(환경 변수 이름만 있고 값이 없는 프로필)은 드롭다운에 보이지 않는다. 모든 프로필의 키를 지우면 [답변 생성]이 꺼지고 [설정] 안내가 나온다.
- [ ] **Step 4: 결과 기록** — 발견한 문제는 수정 Task로 추가한다. 메모리 `project-plan-status.md`에 Plan B 상태를 갱신한다.
