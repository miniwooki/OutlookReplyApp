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
            llm.Delay = TimeSpan.FromMilliseconds(500);
            llm.Enqueue("이전 메일 답변");

            var gen = p.GenerateAsync();
            await Task.Delay(100);
            llm.Delay = TimeSpan.Zero;
            var secondLoad = p.LoadMailAsync(Mails.Create("두 번째 메일", "Fluent 발산"));
            await gen;
            llm.Enqueue(DynaJson);
            await secondLoad;

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
