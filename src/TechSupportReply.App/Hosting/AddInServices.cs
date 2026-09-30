using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.App.Pane;
using TechSupportReply.App.SettingsUi;
using TechSupportReply.Core.Diagnostics;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Rag.Sync;

namespace TechSupportReply.App.Hosting
{
    /// <summary>
    /// 애드인의 조립 루트. 설정·비밀·로그를 소유하고, 지식 세션(카탈로그·검색기)을 첫 사용 시 만든다.
    /// 설정이 바뀌면 세션을 버리고 다음 사용 때 다시 만든다. 사용 중일 수 있는 이전 세션은 종료 시 정리한다.
    /// </summary>
    public sealed class AddInServices : IReplyBackend, ISettingsHost, IDisposable
    {
        private readonly AddInPaths _paths;
        private readonly Func<string, IEmbedder> _embedderFactory;
        private readonly Func<LlmProfile, string, ILlmProvider> _llmFactory;
        private readonly object _buildLock = new object();
        private readonly object _fieldLock = new object();
        private readonly List<KnowledgeSession> _retired = new List<KnowledgeSession>();
        private KnowledgeSession _session;
        private AppSettings _settings;
        private bool _nativeLoaded;
        private int _version;
        private string _appliedRoot;
        private IndexCacheSync _sync;

        /// <summary>테스트용: 세션 구성 중(루트 확정 직후)에 호출된다.</summary>
        internal Action<string> BuildHook { get; set; }

        public AddInServices(AddInPaths paths, Func<string, string> getEnv = null, Func<string, IEmbedder> embedderFactory = null,
            Func<LlmProfile, string, ILlmProvider> llmFactory = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            GetEnv = getEnv ?? EnvironmentVariables.Get;
            _embedderFactory = embedderFactory ?? (dir => new OnnxEmbedder(dir));
            _llmFactory = llmFactory ?? LlmProviderFactory.Create;
            Log = new FileLog(paths.LogDirectory);
            Directory.CreateDirectory(paths.SettingsDirectory);
            SettingsStore = new SettingsStore(paths.SettingsDirectory);
            Secrets = new SecretStore(paths.SettingsDirectory);
            _settings = SettingsStore.Load();
            _appliedRoot = NormalizeRoot(_settings);
            if (DefaultProfileSeeder.SeedFromEnvironment(_settings, GetEnv))
            {
                SettingsStore.Save(_settings);
                Log.Info($"환경 변수에서 LLM 프로필 {_settings.Profiles.Count}개를 만들었습니다.");
            }
            Log.Cleanup();
        }

        public FileLog Log { get; }
        public SettingsStore SettingsStore { get; }
        public SecretStore Secrets { get; }
        public Func<string, string> GetEnv { get; }

        public AppSettings Settings
        {
            get { lock (_fieldLock) return _settings; }
        }

        public void ApplySettings(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            SettingsStore.Save(settings);
            lock (_fieldLock)
            {
                _settings = settings;
                // 세션은 RAG 루트에만 의존한다. 그 외 설정 변경은 데워진 세션을 그대로 쓴다.
                var root = NormalizeRoot(settings);
                if (!string.Equals(root, _appliedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _appliedRoot = root;
                    _version++;
                    if (_session != null) _retired.Add(_session);   // 사용 중일 수 있으므로 종료 시 정리
                    _session = null;
                }
            }
            Log.Info("설정을 저장했습니다.");
        }

        public KnowledgeSession GetSession()
        {
            lock (_fieldLock)
                if (_session != null) return _session;
            lock (_buildLock)
            {
                while (true)
                {
                    AppSettings settings;
                    int version;
                    lock (_fieldLock)
                    {
                        if (_session != null) return _session;
                        settings = _settings;
                        version = _version;
                    }
                    var built = BuildSession(settings);
                    lock (_fieldLock)
                    {
                        if (version == _version)
                        {
                            _session = built;
                            return built;
                        }
                        _retired.Add(built);   // 만드는 사이 RAG 루트가 바뀌었으면 버리고 새 설정으로 다시 만든다
                    }
                }
            }
        }

        public ILlmProvider CreateLlm(string profileId)
        {
            var profile = Settings.ResolveProfile(profileId);
            if (profile == null)
                throw new LlmException(LlmErrorKind.NotConfigured, "등록된 LLM 프로필이 없습니다. [설정] → LLM 프로필에서 프로필을 추가하세요.");
            return new LlmProviderFactory(Secrets, GetEnv, _llmFactory).Create(profile);
        }

        public ILlmProvider CreateLlmForTest(LlmProfile profile, string apiKey) => _llmFactory(profile, apiKey);

        public async Task<string> SyncNowAsync(CancellationToken ct)
        {
            var session = await Task.Run(() => GetSession(), ct).ConfigureAwait(false);
            if (session.Sync == null) return "지식 폴더(RAG 루트)가 설정되지 않았습니다.";
            var ids = session.Catalog.Products.Select(p => p.Id).ToList();
            var r = await Task.Run(() => session.Sync.Sync(ids, ct), ct).ConfigureAwait(false);
            var sb = new StringBuilder(r.SharedReachable ? "공유 폴더와 동기화했습니다." : "공유 폴더에 접근할 수 없어 기존 캐시를 사용합니다.");
            sb.Append(r.UpdatedProducts.Count > 0 ? " 갱신: " + string.Join(", ", r.UpdatedProducts) : " 변경 없음.");
            foreach (var w in r.Warnings) sb.Append("\n⚠ ").Append(w);
            Log.Info("수동 동기화: " + sb.ToString().Replace('\n', ' '));
            return sb.ToString();
        }

        public IndexManifest LoadLocalManifest()
        {
            var root = (Settings.RagRoot ?? "").Trim();
            return root.Length == 0 ? null : SyncFor(root).LoadLocalManifest();
        }

        public void Dispose()
        {
            lock (_fieldLock)
            {
                _session?.Dispose();
                foreach (var s in _retired) s.Dispose();
                _retired.Clear();
                _session = null;
            }
        }

        private KnowledgeSession BuildSession(AppSettings settings)
        {
            var warnings = new List<string>();
            var root = (settings.RagRoot ?? "").Trim();
            if (root.Length == 0)
            {
                warnings.Add("지식 폴더(RAG 루트)가 설정되지 않아 RAG 없이 생성합니다. [설정] → 지식 폴더에서 지정하세요.");
                return new KnowledgeSession(ProductCatalog.CreateDefault(), null, null, warnings);
            }

            EnsureNativeLibraries();
            BuildHook?.Invoke(root);
            var sync = SyncFor(root);
            var catalog = LoadCatalog(root, sync, warnings);
            var retriever = new KnowledgeRetriever(sync, catalog, manifest =>
            {
                var dir = sync.EnsureModel(manifest?.EmbeddingModel, CancellationToken.None);
                return dir == null ? null : _embedderFactory(dir);
            });
            string Guide(ProductDefinition p)
            {
                var path = sync.LocalPromptPath(p.Id);
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            }
            return new KnowledgeSession(catalog, retriever, Guide, warnings, retriever) { Sync = sync };
        }

        private ProductCatalog LoadCatalog(string root, IndexCacheSync sync, List<string> warnings)
        {
            try
            {
                var shared = KbLayout.ProductsPath(root);
                if (File.Exists(shared)) return ProductCatalog.Load(shared);
            }
            catch (Exception ex)
            {
                warnings.Add("공유 폴더의 products.json을 읽지 못했습니다: " + ex.Message);
                Log.Warn("공유 products.json 읽기 실패: " + ex.Message);
            }
            try
            {
                if (File.Exists(sync.LocalProductsPath)) return ProductCatalog.Load(sync.LocalProductsPath);
            }
            catch (Exception ex)
            {
                Log.Warn("캐시 products.json 읽기 실패: " + ex.Message);
            }
            warnings.Add("제품 목록(products.json)을 찾지 못해 기본 제품 목록을 사용합니다.");
            return ProductCatalog.CreateDefault();
        }

        private static string NormalizeRoot(AppSettings settings) => (settings.RagRoot ?? "").Trim();

        /// <summary>같은 캐시 폴더·루트에는 IndexCacheSync 하나를 공유해 동기화가 한 잠금으로 직렬화되게 한다.</summary>
        private IndexCacheSync SyncFor(string root)
        {
            lock (_fieldLock)
            {
                if (_sync == null || !string.Equals(_sync.RagRoot, root, StringComparison.OrdinalIgnoreCase))
                    _sync = new IndexCacheSync(root, _paths.CacheDirectory);
                return _sync;
            }
        }

        private void EnsureNativeLibraries()
        {
            if (_nativeLoaded || _paths.NativeDirectory == null) return;
            _nativeLoaded = true;
            foreach (var w in NativeLibraryPreloader.Preload(_paths.NativeDirectory)) Log.Warn(w);
        }
    }
}
