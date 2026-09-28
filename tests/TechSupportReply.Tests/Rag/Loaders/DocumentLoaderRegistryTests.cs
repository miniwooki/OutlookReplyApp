using System;
using System.Collections.Generic;
using TechSupportReply.Rag.Loaders;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Loaders
{
    public class DocumentLoaderRegistryTests
    {
        private sealed class ThrowingLoader : IDocumentLoader
        {
            public IReadOnlyCollection<string> Extensions => new[] { ".bad" };
            public LoadedDocument Load(string path) => throw new InvalidOperationException("내부 오류");
        }

        [Fact]
        public void CanLoad_ByExtensionCaseInsensitive()
        {
            var registry = DocumentLoaderRegistry.CreateDefault();
            Assert.True(registry.CanLoad(@"C:\kb\FAQ.MD"));
            Assert.True(registry.CanLoad("a.txt"));
            Assert.False(registry.CanLoad("image.png"));
        }

        [Fact]
        public void Load_Unsupported_ThrowsDocumentLoadException()
        {
            var ex = Assert.Throws<DocumentLoadException>(() => DocumentLoaderRegistry.CreateDefault().Load("image.png"));
            Assert.Contains("지원하지 않는", ex.Reason);
            Assert.Contains("image.png", ex.Message);
        }

        [Fact]
        public void Load_LoaderThrows_WrapsWithReason()
        {
            using (var tmp = new TempDir())
            {
                var path = tmp.File("x.bad", "x");
                var ex = Assert.Throws<DocumentLoadException>(() => new DocumentLoaderRegistry(new[] { new ThrowingLoader() }).Load(path));
                Assert.Contains("내부 오류", ex.Reason);
                Assert.Equal(path, ex.FilePath);
            }
        }
    }
}
