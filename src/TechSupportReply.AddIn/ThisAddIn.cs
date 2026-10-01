using System;
using System.Threading;
using System.Windows.Forms;
using TechSupportReply.App.Hosting;
using TechSupportReply.App.SettingsUi;
using TechSupportReply.AddIn.Outlook;
using TechSupportReply.Core.Diagnostics;
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

        /// <summary>
        /// 프레젠터의 await 이후 코드가 UI 스레드로 돌아오도록 WinForms 동기화 컨텍스트를 설치한다.
        /// VSTO 콜백(리본·COM 이벤트)은 SynchronizationContext.Current가 null인 채로 들어올 수 있으므로 진입점마다 호출한다.
        /// </summary>
        internal static void EnsureUiSynchronizationContext()
        {
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }

        internal void ShowSettings()
        {
            EnsureUiSynchronizationContext();
            using (var form = new SettingsForm(Services))
            {
                // 키·프로필을 바꿨을 수 있으므로 저장했으면 열린 작업창의 프로필 목록을 다시 채운다.
                if (form.ShowDialog() == DialogResult.OK) _panes?.RefreshProfiles();
            }
        }

        internal void ReportError(string action, Exception ex)
        {
            try
            {
                // 서비스 생성 자체가 실패했을 수 있으므로 오류 보고 중에는 서비스를 새로 만들지 않는다.
                var log = _services?.Log ?? new FileLog(FileLog.DefaultDirectory);
                log.Error(action + " 실패", ex);
            }
            catch (Exception)
            {
            }
            try
            {
                MessageBox.Show($"{action} 중 오류가 발생했습니다.\n{ex.Message}\n\n로그: %LOCALAPPDATA%\\TechSupportReply\\logs", Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception)
            {
            }
        }

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            // 가볍게 유지한다: 설정·색인·ONNX는 첫 사용 때 만든다.
            try
            {
                EnsureUiSynchronizationContext();
                // Outlook 호스트의 TLS 기본값(Ssl3/Tls 1.0)으로는 LLM API에 연결할 수 없으므로 네트워크 사용 전에 켠다.
                TlsSetup.Apply();
            }
            catch (Exception ex) { ReportError("애드인 시작", ex); }
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
