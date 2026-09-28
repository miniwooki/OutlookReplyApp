using System;
using System.IO;
using System.Text;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Settings
{
    public class SecretStoreTests
    {
        [Fact]
        public void SetThenGet_RoundTripsAcrossInstances()
        {
            using (var tmp = new TempDir())
            {
                new SecretStore(tmp.Root).Set("k1", "sk-test-123");
                Assert.Equal("sk-test-123", new SecretStore(tmp.Root).Get("k1"));
            }
        }

        [Fact]
        public void File_DoesNotContainPlaintext()
        {
            using (var tmp = new TempDir())
            {
                new SecretStore(tmp.Root).Set("k1", "sk-plaintext-marker");
                var bytes = File.ReadAllBytes(Path.Combine(tmp.Root, SecretStore.FileName));
                Assert.DoesNotContain("sk-plaintext-marker", Encoding.UTF8.GetString(bytes));
            }
        }

        [Fact]
        public void Get_Unknown_ReturnsNull()
        {
            using (var tmp = new TempDir())
            {
                Assert.Null(new SecretStore(tmp.Root).Get("nope"));
                Assert.Null(new SecretStore(tmp.Root).Get(null));
            }
        }

        [Fact]
        public void Remove_DeletesKey()
        {
            using (var tmp = new TempDir())
            {
                var store = new SecretStore(tmp.Root);
                store.Set("k1", "v");
                store.Remove("k1");
                Assert.Null(store.Get("k1"));
            }
        }

        [Fact]
        public void Get_CorruptedFile_ThrowsFriendlyError()
        {
            using (var tmp = new TempDir())
            {
                File.WriteAllBytes(Path.Combine(tmp.Root, SecretStore.FileName), new byte[] { 1, 2, 3, 4 });
                var ex = Assert.Throws<InvalidOperationException>(() => new SecretStore(tmp.Root).Get("k1"));
                Assert.Contains("다시 등록", ex.Message);
            }
        }

        [Fact]
        public void Set_OnCorruptedFile_StartsFresh()
        {
            using (var tmp = new TempDir())
            {
                File.WriteAllBytes(Path.Combine(tmp.Root, SecretStore.FileName), new byte[] { 1, 2, 3, 4 });
                var store = new SecretStore(tmp.Root);
                store.Set("k2", "v2");
                Assert.Equal("v2", store.Get("k2"));
            }
        }
    }
}
