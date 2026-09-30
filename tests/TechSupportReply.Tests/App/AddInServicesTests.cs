using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.App.Hosting;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Sync;
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
        public void HasUsableKey_EnvValueOrStoredSecret()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), n => n == "OPENAI_API_KEY" ? "sk" : null);
                services.Secrets.Set("s1", "stored");

                Assert.True(services.HasUsableKey(services.Settings.Profiles.Single()));
                Assert.True(services.HasUsableKey(new LlmProfile { SecretId = "s1" }));
                Assert.False(services.HasUsableKey(new LlmProfile { ApiKeyEnvVar = "XAI_API_KEY" }));
                Assert.False(services.HasUsableKey(new LlmProfile { SecretId = "missing" }));
                Assert.False(services.HasUsableKey(null));
            }
        }

        [Fact]
        public void SaveLastProfile_Persists_WithoutRebuildingSession()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                var session = services.GetSession();

                services.SaveLastProfile("p-xai");

                Assert.Same(session, services.GetSession());
                Assert.Equal("p-xai", services.Settings.LastProfileId);
                Assert.Equal("p-xai", new SettingsStore(Path.Combine(tmp.Root, "settings")).Load().LastProfileId);
            }
        }

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

        private static string MakeKb(TempDir tmp, string name)
        {
            var kb = tmp.Sub(name);
            new ProductCatalog(new[]
            {
                new ProductDefinition { Id = ProductCatalog.CommonId, DisplayName = "공통", Folder = "_common" },
            }).Save(KbLayout.ProductsPath(kb));
            return kb;
        }

        [Fact]
        public void ApplySettings_NonRagChange_SavesAndKeepsSession()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null);
                var first = services.GetSession();
                var s = services.Settings;
                s.User.Name = "홍길동";
                services.ApplySettings(s);
                Assert.Same(first, services.GetSession());
                Assert.Equal("홍길동", new SettingsStore(Path.Combine(tmp.Root, "settings")).Load().User.Name);
            }
        }

        [Fact]
        public void ApplySettings_RagRootChange_RebuildsSession()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null, embedderFactory: _ => new FakeEmbedder());
                var s = services.Settings;
                s.RagRoot = MakeKb(tmp, "kb1");
                services.ApplySettings(s);
                var first = services.GetSession();

                s.RagRoot = MakeKb(tmp, "kb2");
                services.ApplySettings(s);
                var second = services.GetSession();
                Assert.NotSame(first, second);
                Assert.NotSame(first.Sync, second.Sync);
                Assert.Equal(s.RagRoot, second.Sync.RagRoot);
            }
        }

        [Fact]
        public async Task RootChangedDuringBuild_StaleSessionIsNotKept()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null, embedderFactory: _ => new FakeEmbedder());
                var kb1 = MakeKb(tmp, "kb1");
                var kb2 = MakeKb(tmp, "kb2");
                var s = services.Settings;
                s.RagRoot = kb1;
                services.ApplySettings(s);

                var inBuild = new ManualResetEventSlim();
                var release = new ManualResetEventSlim();
                var first = true;
                services.BuildHook = root =>
                {
                    if (!first) return;
                    first = false;
                    inBuild.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                };
                var task = Task.Run(() => services.GetSession());
                Assert.True(inBuild.Wait(TimeSpan.FromSeconds(10)));
                s.RagRoot = kb2;   // 같은 인스턴스를 고쳐 다시 적용
                services.ApplySettings(s);
                release.Set();

                var session = await task;
                Assert.Equal(kb2, session.Sync.RagRoot);
                Assert.Same(session, services.GetSession());
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

        /// <summary>
        /// 공유 폴더(오프라인 UNC 등)를 기다리는 Sync가 IndexCacheSync 잠금을 쥔 상황을 결정적으로 재현한다.
        /// 공개 API로는 Sync를 멈춰 둘 수 없으므로 잠금 객체를 리플렉션으로 직접 잡는다.
        /// </summary>
        private static async Task<T> WhileSyncLockHeld<T>(IndexCacheSync sync, Func<T> action)
        {
            var gate = typeof(IndexCacheSync).GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sync);
            var held = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var holder = Task.Run(() =>
            {
                lock (gate)
                {
                    held.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            });
            try
            {
                Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
                var work = Task.Run(action);
                Assert.Same(work, await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5))));
                return await work;
            }
            finally
            {
                release.Set();
                await holder;
            }
        }

        [Fact]
        public async Task LoadLocalManifest_DoesNotWaitForRunningSync()
        {
            using (var tmp = new TempDir())
            {
                var services = new AddInServices(Paths(tmp), _ => null, embedderFactory: _ => new FakeEmbedder());
                var s = services.Settings;
                s.RagRoot = MakeKb(tmp, "kb");
                services.ApplySettings(s);
                new IndexManifest { EmbeddingModel = "m", Products = { new ProductIndexInfo { ProductId = "ls-dyna", Version = 7 } } }
                    .Save(Path.Combine(tmp.Root, "cache", "index", KbLayout.ManifestFileName));
                var sync = services.GetSession().Sync;

                var manifest = await WhileSyncLockHeld(sync, () => services.LoadLocalManifest());

                Assert.Equal(7, manifest.Find("ls-dyna").Version);
            }
        }

        [Fact]
        public async Task SyncNow_CopiesEmbeddingModel_AndReportsReady()
        {
            using (var tmp = new TempDir())
            {
                var kb = MakeKb(tmp, "kb");
                new IndexManifest { EmbeddingModel = "fake-model", Dimension = 64 }.Save(KbLayout.ManifestPath(kb));
                tmp.File("kb/_models/fake-model/model.onnx", "onnx");
                tmp.File("kb/_models/fake-model/sentencepiece.bpe.model", "spm");
                var services = new AddInServices(Paths(tmp), _ => null, embedderFactory: _ => new FakeEmbedder());
                var s = services.Settings;
                s.RagRoot = kb;
                services.ApplySettings(s);

                var message = await services.SyncNowAsync(CancellationToken.None);

                Assert.Contains("임베딩 모델: 준비됨(fake-model)", message);
                Assert.Equal("onnx", File.ReadAllText(Path.Combine(tmp.Root, "cache", "models", "fake-model", "model.onnx")));
            }
        }

        [Fact]
        public async Task SyncNow_MissingEmbeddingModel_Warns()
        {
            using (var tmp = new TempDir())
            {
                var kb = MakeKb(tmp, "kb");
                new IndexManifest { EmbeddingModel = "fake-model", Dimension = 64 }.Save(KbLayout.ManifestPath(kb));
                var services = new AddInServices(Paths(tmp), _ => null, embedderFactory: _ => new FakeEmbedder());
                var s = services.Settings;
                s.RagRoot = kb;
                services.ApplySettings(s);

                var message = await services.SyncNowAsync(CancellationToken.None);

                Assert.Contains("임베딩 모델: 없음", message);
                Assert.Contains(@"_models\fake-model", message);
            }
        }
    }
}
