using System;
using System.Collections.Generic;
using TechSupportReply.Core.Settings;
using Xunit;

namespace TechSupportReply.Tests.Core.Settings
{
    public class EnvironmentVariablesTests
    {
        private static Func<string, EnvironmentVariableTarget?, string> Lookup(string process, string user, string machine) =>
            (name, target) => target == null ? process : target == EnvironmentVariableTarget.User ? user : machine;

        [Fact]
        public void PrefersProcessValue()
        {
            Assert.Equal("p", EnvironmentVariables.Get("K", Lookup("p", "u", "m")));
        }

        [Fact]
        public void FallsBackToUserThenMachine()
        {
            Assert.Equal("u", EnvironmentVariables.Get("K", Lookup(null, "u", "m")));
            Assert.Equal("m", EnvironmentVariables.Get("K", Lookup("  ", null, "m")));
        }

        [Fact]
        public void TrimsValue_AndReturnsNullWhenMissing()
        {
            Assert.Equal("abc", EnvironmentVariables.Get("K", Lookup(" abc \r\n", null, null)));
            Assert.Null(EnvironmentVariables.Get("K", Lookup(null, null, null)));
            Assert.Null(EnvironmentVariables.Get(" ", Lookup("p", "u", "m")));
        }
    }
}
