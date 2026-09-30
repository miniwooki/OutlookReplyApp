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
