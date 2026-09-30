using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.App.Pane;
using TechSupportReply.Core.Knowledge;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class ReplyPanePresenterTests
    {
        private const string DynaJson = "{\"productId\":\"ls-dyna\",\"confidence\":0.9,\"reason\":\"LS-DYNA 접촉 문의\"}";
        private static readonly global::TechSupportReply.Core.Models.MailSnapshot Mail =
            Mails.Create("접촉 경고 문의", "*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE 초기 관통 경고가 d3hsp에 나옵니다.");

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

            Assert.Equal(ReplyPanePresenter.NoUsableProfileMessage, view.Status);

            backend.KeyCheck = null;
            await p.RefreshProfilesAsync();

            Assert.True(view.GenerateAvailable);
            Assert.Equal(new[] { "p1" }, view.ProfileIds);
            Assert.Equal("p1", view.SelectedProfileId);
            Assert.False(view.StatusIsError);
            Assert.Contains("[답변 생성]", view.Status);

            // 키를 지우고 설정을 저장하면 [답변 생성]이 꺼지고 안내가 다시 보인다.
            backend.KeyCheck = _ => false;
            await p.RefreshProfilesAsync();

            Assert.False(view.GenerateAvailable);
            Assert.Equal(ReplyPanePresenter.NoUsableProfileMessage, view.Status);
            Assert.True(view.StatusIsError);
        }

        [Fact]
        public async Task RefreshProfiles_WithKeys_KeepsOtherStatus()
        {
            var view = new FakePaneView();
            var backend = new FakeBackend(new FakeLlmProvider()) { CreateLlmThrows = new LlmException(LlmErrorKind.NotConfigured, "키 없음") };
            var p = new ReplyPanePresenter(view, backend);
            await p.LoadMailAsync(Mail);
            var status = view.Status;
            Assert.Contains("키 없음", status);

            await p.RefreshProfilesAsync();

            Assert.Equal(status, view.Status);
        }

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

            // 호출 스레드를 붙잡아 둔 채 GetSession이 시작되기를 기다린다. 테스트 스레드도 스레드 풀 스레드일 수 있어서,
            // 먼저 풀어 주면 풀어 준 스레드가 GetSession을 넘겨받아 같은 스레드 ID가 나올 수 있다.
            Assert.True(backend.SessionEntered.Wait(TimeSpan.FromSeconds(10)), "GetSession이 시작되지 않았습니다.");
            Assert.NotEqual(callerThread, backend.GetSessionThreadId);

            backend.SessionGate.Set();
            await task;
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
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (view.ProductIds.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.NotEmpty(view.ProductIds);
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
            Assert.Contains("영어로 답해 주세요", string.Join("\n", llm.Requests.Last().Messages.Select(m => m.Content)));
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

            // 첫 메일의 생성 호출은 30초 지연된다. 취소되지 않으면 테스트가 시간 초과로 실패한다.
            llm.Delay = TimeSpan.FromSeconds(30);
            llm.Enqueue("이전 메일 답변");
            var gen = p.GenerateAsync();
            await WaitUntil(() => llm.Requests.Count == 2);
            Assert.False(llm.Tokens[1].IsCancellationRequested);

            // 둘째 메일의 분류 호출은 지연 없이 자기 응답을 받는다.
            llm.Delay = TimeSpan.Zero;
            llm.Enqueue(DynaJson);
            await p.LoadMailAsync(Mails.Create("두 번째 메일", "Fluent 발산"));

            Assert.True(llm.Tokens[1].IsCancellationRequested);
            Assert.True(await Task.WhenAny(gen, Task.Delay(5000)) == gen, "이전 생성이 취소되지 않았다");
            await gen;

            Assert.Equal("두 번째 메일", view.Subject);
            Assert.Equal("ls-dyna", view.SelectedProductId);
            Assert.Equal("", view.ReplyText);
            Assert.DoesNotContain("중지", view.Status ?? "");
            Assert.Equal(PaneState.Idle, view.State);
        }

        [Fact]
        public async Task Generate_ViewFailureBeforeLlmCall_IsReported_AndStateRecovers()
        {
            var view = new FakePaneView();
            var backend = new FakeBackend(new FakeLlmProvider().Enqueue(DynaJson).Enqueue("답"));
            var p = new ReplyPanePresenter(view, backend);
            await p.LoadMailAsync(Mail);

            view.ThrowOnNextClearReply = true;
            view.ClickGenerate();
            await WaitUntil(() => view.StatusIsError);

            Assert.Contains("화면 갱신 실패", view.Status);
            Assert.Contains("화면 갱신 실패", backend.LogText);
            Assert.Equal(PaneState.Idle, p.State);

            await p.GenerateAsync();
            Assert.Equal("답", view.ReplyText);
        }

        [Fact]
        public async Task Generate_TimeoutCancellation_IsReportedAsError_NotAsStop()
        {
            var view = new FakePaneView();
            var llm = new FakeLlmProvider().Enqueue(DynaJson);
            var backend = new FakeBackend(llm);
            var p = new ReplyPanePresenter(view, backend);
            await p.LoadMailAsync(Mail);
            llm.ThrowOnCall = new TaskCanceledException("HttpClient timeout");

            await p.GenerateAsync();

            Assert.True(view.StatusIsError);
            Assert.DoesNotContain("중지", view.Status);
            Assert.Contains("HttpClient timeout", backend.LogText);
            Assert.Equal(PaneState.Idle, view.State);
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(condition(), "조건이 시간 안에 충족되지 않았다");
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
