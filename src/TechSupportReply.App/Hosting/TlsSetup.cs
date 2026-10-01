using System.Net;

namespace TechSupportReply.App.Hosting
{
    /// <summary>
    /// OUTLOOK.EXE에는 .NET 대상 버전 정보가 없어서 애드인 AppDomain의 TLS 기본값이 예전 호환 모드(Ssl3, Tls 1.0)가 된다.
    /// LLM API는 TLS 1.2 이상만 받으므로 그대로 두면 "SSL/TLS 보안 채널을 만들 수 없습니다"로 모든 호출이 실패한다.
    /// ServicePointManager는 AppDomain마다 따로 있으므로 다른 애드인에는 영향이 없다.
    /// </summary>
    public static class TlsSetup
    {
        /// <summary>OS 기본값(SystemDefault)이거나 이미 TLS 1.2를 쓰면 그대로 두고, 아니면 TLS 1.2·1.3을 켠다.</summary>
        public static SecurityProtocolType WithModernTls(SecurityProtocolType current)
        {
            if (current == SecurityProtocolType.SystemDefault || current.HasFlag(SecurityProtocolType.Tls12)) return current;
            return current | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        }

        /// <summary>애드인 시작 시 한 번 호출한다.</summary>
        public static void Apply() =>
            ServicePointManager.SecurityProtocol = WithModernTls(ServicePointManager.SecurityProtocol);
    }
}
