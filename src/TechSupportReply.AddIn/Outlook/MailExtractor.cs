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
    /// <summary>
    /// UI 스레드에서 MailItem을 읽어 COM과 무관한 MailSnapshot으로 복사한다. 텍스트 첨부는 임시 폴더에 저장해 읽고 지운다.
    /// 읽는 동안 얻은 Attachments·Attachment·AddressEntry·ExchangeUser는 복사 직후 해제한다. MailItem 자체의 해제는 호출자 몫이다.
    /// </summary>
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

        /// <summary>
        /// 메일이 들어 있는 저장소(사서함·PST)의 StoreID. 공유 사서함·보조 저장소의 메일은 GetItemFromID에 이 값이 있어야 찾을 수 있다.
        /// 폴더가 아니거나(예: 파일로 연 .msg) 읽지 못하면 null. 얻은 폴더 RCW는 바로 해제한다.
        /// </summary>
        public static string StoreIdOf(OutlookApi.MailItem mail, FileLog log)
        {
            object parent = null;
            try
            {
                parent = mail.Parent;
                return parent is OutlookApi.MAPIFolder folder ? folder.StoreID : null;
            }
            catch (COMException ex)
            {
                log.Warn($"메일 저장소 ID를 읽지 못했습니다(기본 저장소에서 찾습니다): {ex.Message}");
                return null;
            }
            finally
            {
                if (parent != null && Marshal.IsComObject(parent)) Marshal.ReleaseComObject(parent);
            }
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
