using TechSupportReply.Core.Settings;
using Xunit;

namespace TechSupportReply.Tests.Core.Settings
{
    public class AppSettingsTests
    {
        private static AppSettings With(string defaultId, params string[] ids)
        {
            var s = new AppSettings { DefaultProfileId = defaultId };
            foreach (var id in ids) s.Profiles.Add(new LlmProfile { Id = id });
            return s;
        }

        [Fact]
        public void ResolveProfile_ReturnsMatchingId()
        {
            Assert.Equal("b", With("a", "a", "b").ResolveProfile("b").Id);
        }

        [Fact]
        public void ResolveProfile_UnknownOrNullId_FallsBackToDefault()
        {
            var s = With("b", "a", "b");
            Assert.Equal("b", s.ResolveProfile("zzz").Id);
            Assert.Equal("b", s.ResolveProfile(null).Id);
        }

        [Fact]
        public void ResolveProfile_NoDefault_FallsBackToFirst_AndNullWhenEmpty()
        {
            Assert.Equal("a", With("", "a", "b").ResolveProfile("zzz").Id);
            Assert.Null(new AppSettings().ResolveProfile("x"));
        }
    }
}
