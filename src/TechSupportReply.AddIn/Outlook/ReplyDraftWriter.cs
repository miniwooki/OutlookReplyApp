using System.Runtime.InteropServices;
using TechSupportReply.Core.Text;
using OutlookApi = Microsoft.Office.Interop.Outlook;

namespace TechSupportReply.AddIn.Outlook
{
    /// <summary>원본에 [전체 회신] 초안을 만들고 답변을 맨 앞에 넣어 창을 연다. 발송하지 않는다.</summary>
    internal static class ReplyDraftWriter
    {
        /// <remarks>
        /// 호출할 때마다 ReplyAll()로 새 회신 항목을 만들고 그 항목에 정확히 한 번만 넣으므로 같은 초안에 답변이 두 번 들어가지 않는다.
        /// 본문에 이미 <c>tsr-reply</c> 블록이 있는지로 막지 않는다: 이전에 우리가 보낸 답변이 인용 이력에 들어 있으면 오탐이 된다.
        /// </remarks>
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
