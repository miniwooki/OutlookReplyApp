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
            public ReplyTaskPaneControl Control;
            public ReplyPanePresenter Presenter;
            public string EntryId;
            /// <summary>원본 메일의 저장소 ID. 없으면(null) 기본 저장소에서 찾는다.</summary>
            public string StoreId;
            public Action Unsubscribe;
        }

        private readonly ThisAddIn _addIn;
        // 키는 리본 콜백이 준 Explorer/Inspector RCW다. 같은 창은 같은 RCW로 오므로 참조 비교로 찾는다. 창 RCW는 Outlook이 준 것이라 해제하지 않는다.
        private readonly Dictionary<object, PaneEntry> _panes = new Dictionary<object, PaneEntry>();

        public TaskPaneManager(ThisAddIn addIn)
        {
            _addIn = addIn;
        }

        public void RunForContext(object context)
        {
            ThisAddIn.EnsureUiSynchronizationContext();
            var mail = GetMail(context, out var window);
            if (mail == null)
            {
                MessageBox.Show("메일을 하나 선택한 뒤 다시 누르세요.", ThisAddIn.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            MailSnapshot snapshot;
            string entryId;
            string storeId;
            try
            {
                snapshot = MailExtractor.Extract(mail, _addIn.Services.Log);
                entryId = mail.EntryID;
                storeId = MailExtractor.StoreIdOf(mail, _addIn.Services.Log);
            }
            finally
            {
                Marshal.ReleaseComObject(mail);
            }

            var entry = GetOrCreate(window);
            entry.EntryId = entryId;
            entry.StoreId = storeId;
            entry.Pane.Visible = true;
            // Post는 핸들이 없으면 스트리밍 조각을 버린다. 프레젠터를 쓰기 전에 핸들을 만들어 둔다.
            _ = entry.Control.Handle;
            _ = entry.Presenter.LoadMailAsync(snapshot);
        }

        /// <summary>설정을 저장한 뒤 호출한다. RefreshProfilesAsync는 예외를 던지지 않는다.</summary>
        public void RefreshProfiles()
        {
            foreach (var entry in _panes.Values.ToList()) _ = entry.Presenter.RefreshProfilesAsync();
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
            {
                var current = inspector.CurrentItem;
                if (current is OutlookApi.MailItem m) return m;
                if (current != null && Marshal.IsComObject(current)) Marshal.ReleaseComObject(current);
                return null;
            }
            return null;
        }

        private PaneEntry GetOrCreate(object window)
        {
            if (_panes.TryGetValue(window, out var existing)) return existing;

            var control = new ReplyTaskPaneControl();
            // 프레젠터보다 먼저 구독해 생성 버튼의 async 흐름이 시작되기 전에 UI 동기화 컨텍스트를 보장한다(구독 순서대로 호출된다).
            control.GenerateRequested += (s, e) =>
            {
                try { ThisAddIn.EnsureUiSynchronizationContext(); }
                catch (Exception ex) { _addIn.Services.Log.Warn("동기화 컨텍스트 설치 실패: " + ex.Message); }
            };
            var entry = new PaneEntry { Control = control, Presenter = new ReplyPanePresenter(control, _addIn.Services) };
            // 예외는 ReplyPanePresenter.RequestDraft가 잡아 로그와 작업창 상태 메시지로 보여 준다.
            entry.Presenter.DraftReady += text => CreateDraft(entry, text);
            control.SettingsRequested += (s, e) =>
            {
                try { _addIn.ShowSettings(); }
                catch (Exception ex) { _addIn.ReportError("설정 열기", ex); }
            };
            entry.Pane = _addIn.CustomTaskPanes.Add(control, ThisAddIn.Title, window);
            entry.Pane.Width = 460;
            _panes[window] = entry;

            void OnClose()
            {
                try { Remove(window); }
                catch (Exception ex) { _addIn.ReportError("작업창 정리", ex); }
            }
            if (window is OutlookApi.Inspector inspector)
            {
                var events = (OutlookApi.InspectorEvents_10_Event)inspector;
                OutlookApi.InspectorEvents_10_CloseEventHandler handler = OnClose;
                events.Close += handler;
                entry.Unsubscribe = () => events.Close -= handler;
            }
            else if (window is OutlookApi.Explorer explorer)
            {
                var events = (OutlookApi.ExplorerEvents_10_Event)explorer;
                OutlookApi.ExplorerEvents_10_CloseEventHandler handler = OnClose;
                events.Close += handler;
                entry.Unsubscribe = () => events.Close -= handler;
            }
            return entry;
        }

        private void CreateDraft(PaneEntry entry, string text)
        {
            if (string.IsNullOrEmpty(entry.EntryId)) throw new InvalidOperationException("원본 메일의 ID가 없습니다. 메일을 다시 선택하세요.");
            OutlookApi.NameSpace session = null;
            object item = null;
            try
            {
                session = _addIn.Application.Session;
                // 공유 사서함·PST의 메일은 StoreID 없이 찾으면 기본 저장소만 뒤져 "찾을 수 없음"이 된다.
                item = string.IsNullOrEmpty(entry.StoreId)
                    ? session.GetItemFromID(entry.EntryId)
                    : session.GetItemFromID(entry.EntryId, entry.StoreId);
                if (!(item is OutlookApi.MailItem mail)) throw new InvalidOperationException("원본 메일을 찾을 수 없습니다(이동 또는 삭제되었을 수 있습니다).");
                ReplyDraftWriter.CreateReplyAll(mail, text);
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
                if (session != null) Marshal.ReleaseComObject(session);
            }
        }

        private void Remove(object window)
        {
            if (!_panes.TryGetValue(window, out var entry)) return;
            _panes.Remove(window);
            try
            {
                entry.Unsubscribe?.Invoke();
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
