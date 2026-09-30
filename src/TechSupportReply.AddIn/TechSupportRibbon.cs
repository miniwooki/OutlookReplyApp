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
        public string GetCustomUI(string ribbonID)
        {
            try { return RibbonMarkup.Get(ribbonID); }
            catch (Exception ex)
            {
                Globals.ThisAddIn.ReportError("리본 불러오기", ex);
                return null;
            }
        }

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
