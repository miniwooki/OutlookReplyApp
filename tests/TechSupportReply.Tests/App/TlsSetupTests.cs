using System.Net;
using TechSupportReply.App.Hosting;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class TlsSetupTests
    {
        [Fact]
        public void LegacyHostDefault_GetsTls12AndTls13()
        {
            // OUTLOOK.EXE처럼 .NET 대상 버전 정보가 없는 호스트의 기본값(2026-10-01 실제 로그).
            var result = TlsSetup.WithModernTls(SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls);

            Assert.True(result.HasFlag(SecurityProtocolType.Tls12));
            Assert.True(result.HasFlag(SecurityProtocolType.Tls13));
        }

        [Fact]
        public void SystemDefault_IsLeftToOperatingSystem()
        {
            Assert.Equal(SecurityProtocolType.SystemDefault, TlsSetup.WithModernTls(SecurityProtocolType.SystemDefault));
        }

        [Fact]
        public void AlreadyHasTls12_IsUnchanged()
        {
            var current = SecurityProtocolType.Tls12;
            Assert.Equal(current, TlsSetup.WithModernTls(current));
        }
    }
}
