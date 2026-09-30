using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.App.Hosting;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.App
{
    public class AddInServicesTests
    {
        private static AddInPaths Paths(TempDir tmp) => new AddInPaths
        {
            SettingsDirectory = tmp.Sub("settings"),
            CacheDirectory = tmp.Sub("cache"),
            LogDirectory = tmp.Sub("logs"),
            NativeDirectory = null,
        };

        [Fact]
        public void FirstRun_SeedsProfilesFromEnvironment_AndSaves()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), n => n == "OPENAI_API_KEY" ? "sk" : null);
                Assert.Single(services.Settings.Profiles);
                Assert.Equal("OPENAI_API_KEY", services.Settings.Profiles[0].ApiKeyEnvVar);
                Assert.Single(new SettingsStore(tmp.Root + "\\settings").Load().Profiles);
            }
        }

        [Fact]
        public void NoRagRoot_SessionUsesDefaultCatalogWithoutRetriever_AndWarns()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                var session = services.GetSession();
                Assert.Null(session.Retriever);
                Assert.Equal(ProductCatalog.CreateDefault().Products.Count, session.Catalog.Products.Count);
                Assert.Contains(session.Warnings, w => w.Contains("RAG 루트"));
                Assert.Same(session, services.GetSession());
            }
        }

        [Fact]
        public void RagRoot_LoadsSharedProductsJson_AndCreatesRetriever()
        {
            using (var tmp = new TempDir())
            {
                var kb = tmp.Sub("kb");
                new ProductCatalog(new[]
                {
                    new ProductDefinition { Id = ProductCatalog.CommonId, DisplayName = "공통", Folder = "_common" },
                    new ProductDefinition { Id = "only-one", DisplayName = "하나", Folder = "One" },
                }).Save(KbLayout.ProductsPath(kb));
                var services = new AddInServices(Paths(tmp), _ => null, embedderFactory: _ => new FakeEmbedder());
                var s = services.Settings;
                s.RagRoot = kb;
                services.ApplySettings(s);

                var session = services.GetSession();
                Assert.NotNull(session.Retriever);
                Assert.NotNull(session.Sync);
                Assert.NotNull(session.Catalog.Find("only-one"));
            }
        }

        [Fact]
        public void ApplySettings_SavesAndRebuildsSession()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                var first = services.GetSession();
                var s = services.Settings;
                s.User.Name = "홍길동";
                services.ApplySettings(s);
                Assert.NotSame(first, services.GetSession());
                Assert.Equal("홍길동", new SettingsStore(Path.Combine(tmp.Root, "settings")).Load().User.Name);
            }
        }

        [Fact]
        public void CreateLlm_UsesEnvKey_AndFallsBackToDefaultProfile()
        {
            using (var tmp = new TempDir())
            {
                string seenKey = null;
                LlmProfile seenProfile = null;
                var services = new AddInServices(Paths(tmp), n => n == "XAI_API_KEY" ? "xai-key" : null,
                    llmFactory: (p, k) => { seenProfile = p; seenKey = k; return new FakeLlmProvider(); });
                services.CreateLlm("unknown-id");
                Assert.Equal("xai-key", seenKey);
                Assert.Equal("grok-4", seenProfile.Model);
            }
        }

        [Fact]
        public void CreateLlm_NoProfiles_ThrowsNotConfigured()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                var ex = Assert.Throws<LlmException>(() => services.CreateLlm(null));
                Assert.Equal(LlmErrorKind.NotConfigured, ex.Kind);
                Assert.Contains("설정", ex.UserMessage);
            }
        }

        [Fact]
        public async Task SyncNow_WithoutRagRoot_ReportsNotConfigured()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                Assert.Contains("설정되지", await services.SyncNowAsync(CancellationToken.None));
                Assert.Null(services.LoadLocalManifest());
            }
        }
    }
}
