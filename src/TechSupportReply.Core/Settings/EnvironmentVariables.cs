using System;
using System.Security;

namespace TechSupportReply.Core.Settings
{
    /// <summary>
    /// 환경 변수를 프로세스 → 사용자 → 시스템 순서로 읽는다. Outlook이 실행된 뒤 추가한 시스템 환경 변수는
    /// 프로세스 환경에 없으므로 레지스트리 값으로 대체한다.
    /// </summary>
    public static class EnvironmentVariables
    {
        private static readonly EnvironmentVariableTarget?[] Order =
            { null, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine };

        public static string Get(string name) =>
            Get(name, (n, target) => target == null ? Environment.GetEnvironmentVariable(n) : Environment.GetEnvironmentVariable(n, target.Value));

        internal static string Get(string name, Func<string, EnvironmentVariableTarget?, string> lookup)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var target in Order)
            {
                string value;
                try { value = lookup(name.Trim(), target); }
                catch (SecurityException) { value = null; }
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }
    }
}
