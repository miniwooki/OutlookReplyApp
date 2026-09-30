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
            _view.GenerateRequested += async (s, e) =>
            {
                try { await GenerateAsync(); }
                catch (Exception ex) { ReportHandlerFailure("답변 생성 요청", ex); }
            };
            _view.StopRequested += (s, e) => Guard("생성 중지", Stop);
            _view.DraftRequested += (s, e) => Guard("회신 초안 요청", RequestDraft);
            _view.ProductChangedByUser += (s, e) => _userChoseProduct = true;
        }

        /// <summary>이벤트 핸들러에서 예외가 새어 나가 Outlook을 흔들지 않도록 로그와 한국어 메시지로 바꾼다.</summary>
        private void Guard(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { ReportHandlerFailure(what, ex); }
        }

        private void ReportHandlerFailure(string what, Exception ex)
        {
            try
            {
                _backend.Log.Error(what + " 실패", ex);
                _view.SetStatus(what + " 중 오류: " + ex.Message, true);
            }
            catch
            {
            }
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

            try
            {
                _view.ShowMail(mail.Subject, string.IsNullOrEmpty(mail.SenderEmail) ? mail.SenderName : $"{mail.SenderName} <{mail.SenderEmail}>");
                _view.ClearReply();
                _view.SetReferences(None);
                _view.SetWarnings(None);
                SetState(PaneState.Classifying);
                _view.SetStatus("제품군을 판별하는 중…", false);

                var settings = _backend.Settings;
                _view.SetProfiles(settings.Profiles, settings.ResolveProfile(settings.DefaultProfileId)?.Id);
                var session = await Task.Run(() => _backend.GetSession(), cts.Token);
                if (version != _mailVersion) return;

                var classifier = new ProductClassifier(session.Catalog);
                var keyword = classifier.ClassifyByKeywords(mail);
                _view.SetProducts(session.Catalog.Products, keyword.ProductId);
                _view.SetClassification(Describe(session.Catalog, keyword));

                ILlmProvider llm = null;
                string llmProblem = null;
                var classifierProfile = settings.ResolveProfile(settings.ClassifierProfileId);
                if (classifierProfile != null)
                {
                    try { llm = _backend.CreateLlm(classifierProfile.Id); }
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
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
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
            var mail = _mail;
            try
            {
                SetState(PaneState.Generating);
                _view.ClearReply();
                _view.SetReferences(None);
                _view.SetWarnings(None);
                _view.SetStatus("답변을 생성하는 중… (중지하려면 [중지])", false);

                var request = new ReplyRequest
                {
                    Mail = mail,
                    ProductId = _view.SelectedProductId ?? ProductCatalog.CommonId,
                    ExtraInstruction = _view.ExtraInstruction ?? "",
                    UseRag = _view.UseRag,
                };
                var profileId = _view.SelectedProfileId;
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
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
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

        private static string Describe(ProductCatalog catalog, ClassificationResult r)
        {
            var name = catalog.Find(r.ProductId)?.DisplayName ?? r.ProductId;
            var source = r.Source == ClassificationSource.Llm ? "LLM" : "키워드";
            var reason = string.IsNullOrWhiteSpace(r.Reason) ? "" : " — " + r.Reason;
            return $"{name} · 신뢰도 {r.Confidence:0.00} · {source}{reason}";
        }
    }
}
