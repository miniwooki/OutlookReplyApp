using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace TechSupportReply.Tests.TestSupport
{
    /// <summary>WinForms 컨트롤 테스트를 STA 스레드에서 실행한다.</summary>
    internal static class Sta
    {
        public static void Run(Action action)
        {
            Exception error = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
