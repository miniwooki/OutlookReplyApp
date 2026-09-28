using System;
using System.Linq;
using TechSupportReply.Core.Models;

namespace TechSupportReply.Tests.TestSupport
{
    internal static class Mails
    {
        public static MailSnapshot Create(string subject, string body, params string[] attachments) =>
            new MailSnapshot
            {
                Subject = subject,
                Body = body,
                SenderName = "김고객",
                SenderEmail = "customer@example.com",
                ReceivedAt = new DateTime(2026, 9, 1, 10, 0, 0),
                AttachmentNames = attachments.ToList(),
            };
    }
}
