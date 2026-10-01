using System;
using System.Collections.Generic;

namespace TechSupportReply.Core.Models
{
    /// <summary>Outlook COM에 의존하지 않는 메일 사본. 애드인은 UI 스레드에서 이 객체로 복사한 뒤 COM 객체를 놓는다.</summary>
    public sealed class MailSnapshot
    {
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string SenderEmail { get; set; } = "";
        public DateTime ReceivedAt { get; set; }
        public List<string> AttachmentNames { get; set; } = new List<string>();
        public string AttachmentText { get; set; } = "";
    }
}
