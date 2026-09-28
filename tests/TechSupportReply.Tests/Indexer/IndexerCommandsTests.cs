using System.Collections.Generic;
using System.IO;
using TechSupportReply.Core.Llm;
using TechSupportReply.Indexer;
using TechSupportReply.Rag.Indexing;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Indexer
{
    public class CliArgsTests
    {
        [Fact]
        public void CliArgs_ParsesCommandOptionsAndFlags()
        {
            var a = CliArgs.Parse(new[] { "INDEX", "--root", @"\\server\KB", "--full", "--top=5", "--product", "ls-dyna" });
            Assert.Equal("index", a.Command);
            Assert.Equal(@"\\server\KB", a.Get("root"));
            Assert.Equal("ls-dyna", a.Get("product"));
            Assert.True(a.Has("full"));
            Assert.False(a.Has("root"));
            Assert.Equal(5, a.GetInt("top", 8));
            Assert.Equal(8, a.GetInt("missing", 8));
            Assert.Equal("x", a.Get("missing", "x"));
        }

        [Fact]
        public void CliArgs_Require_MissingThrows()
        {
            var ex = Assert.Throws<CliException>(() => CliArgs.Parse(new[] { "search" }).Require("query"));
            Assert.Contains("--query", ex.Message);
        }
    }

    public class IndexerCommandsTests
    {
        private static IndexerEnvironment FakeEnv(FakeLlmProvider llm = null) => new IndexerEnvironment
        {
            CreateEmbedder = dir => new FakeEmbedder(),
            GetEnv = name => name == "ANTHROPIC_API_KEY" ? "sk-test" : null,
            CreateLlm = (profile, key) => llm ?? new FakeLlmProvider(),
        };

        private static (int Code, string Output) Run(IndexerEnvironment env, params string[] args)
        {
            var output = new StringWriter();
            var code = Program.Run(args, output, env);
            return (code, output.ToString());
        }

        private static string FakeModel(TempDir tmp)
        {
            tmp.File("kb/_models/fake/model.onnx", "x");
            tmp.File("kb/_models/fake/sentencepiece.bpe.model", "x");
            return Path.Combine(tmp.Root, "kb", "_models", "fake");
        }

        [Fact]
        public void InitKb_CreatesStructureWithoutOverwriting()
        {
            using (var tmp = new TempDir())
            {
                var root = Path.Combine(tmp.Root, "kb");
                tmp.File("kb/LS-DYNA/_prompt.md", "기존 지침");
                var (code, output) = Run(FakeEnv(), "init-kb", "--root", root);
                Assert.Equal(0, code);
                Assert.Equal("기존 지침", File.ReadAllText(Path.Combine(root, "LS-DYNA", "_prompt.md")));
                Assert.True(File.Exists(KbLayout.ProductsPath(root)));
                Assert.True(File.Exists(Path.Combine(root, "README.md")));
                Assert.True(Directory.Exists(Path.Combine(root, "Ansys-Fluent", "replies")));
                Assert.True(Directory.Exists(Path.Combine(root, "_common", "faq")));
                Assert.True(Directory.Exists(Path.Combine(root, "_index")));
                Assert.Contains("Ansys Fluent", File.ReadAllText(Path.Combine(root, "Ansys-Fluent", "_prompt.md")));
                Assert.Contains("생성", output);
            }
        }

        [Fact]
        public void Index_ThenSearch_EndToEndWithFakeEmbedder()
        {
            using (var tmp = new TempDir())
            {
                var root = Path.Combine(tmp.Root, "kb");
                Run(FakeEnv(), "init-kb", "--root", root);
                tmp.File("kb/LS-DYNA/faq/contact.md", "# 접촉\n초기 관통 경고는 IGNORE=1로 처리합니다.");
                tmp.File("kb/LS-DYNA/replies/r1.txt", "질문:\n접촉 경고\n\n답변:\n안녕하세요. IGNORE=1을 권장합니다.");
                tmp.File("kb/Ansys-Fluent/faq/conv.md", "# 수렴\n완화 계수를 낮춥니다.");
                var model = FakeModel(tmp);

                var index = Run(FakeEnv(), "index", "--root", root, "--model", model, "--work", Path.Combine(tmp.Root, "work"));
                Assert.Equal(0, index.Code);
                Assert.Contains("ls-dyna", index.Output);
                Assert.Contains("추가 2", index.Output);
                Assert.NotNull(IndexManifest.Load(KbLayout.ManifestPath(root)).Find("ansys-fluent"));

                var search = Run(FakeEnv(), "search", "--root", root, "--product", "ls-dyna", "--query", "접촉 관통", "--cache", Path.Combine(tmp.Root, "cache"));
                Assert.Equal(0, search.Code);
                Assert.Contains("contact.md", search.Output);
                Assert.Contains("r1.txt", search.Output);
                Assert.DoesNotContain("conv.md", search.Output);
            }
        }

        [Fact]
        public void Index_ReportsSkippedFiles()
        {
            using (var tmp = new TempDir())
            {
                var root = Path.Combine(tmp.Root, "kb");
                Run(FakeEnv(), "init-kb", "--root", root);
                File.Copy(TestPaths.Fixture("docs", "encrypted.pdf"), Path.Combine(root, "LS-DYNA", "manuals", "encrypted.pdf"));
                var (code, output) = Run(FakeEnv(), "index", "--root", root, "--product", "ls-dyna", "--model", FakeModel(tmp), "--work", Path.Combine(tmp.Root, "work"));
                Assert.Equal(0, code);
                Assert.Contains("encrypted.pdf", output);
                Assert.Contains("암호", output);
            }
        }

        [Fact]
        public void Reply_WithFakeLlm_StreamsAnswerAndReferences()
        {
            using (var tmp = new TempDir())
            {
                var root = Path.Combine(tmp.Root, "kb");
                Run(FakeEnv(), "init-kb", "--root", root);
                tmp.File("kb/LS-DYNA/faq/contact.md", "# 접촉\n초기 관통 경고는 IGNORE=1로 처리합니다.");
                Run(FakeEnv(), "index", "--root", root, "--model", FakeModel(tmp), "--work", Path.Combine(tmp.Root, "work"));
                var mail = tmp.File("mail.txt", "LS-DYNA 접촉 관통 문의\n*CONTACT 사용 시 초기 관통 경고가 d3hsp에 나옵니다.");

                var llm = new FakeLlmProvider().Enqueue("{\"productId\":\"ls-dyna\",\"confidence\":0.9,\"reason\":\"접촉 문의\"}").Enqueue("안녕하세요. IGNORE=1을 검토해 보세요.");
                var (code, output) = Run(FakeEnv(llm), "reply", "--root", root, "--mail", mail, "--cache", Path.Combine(tmp.Root, "cache"));
                Assert.Equal(0, code);
                Assert.Contains("ls-dyna", output);
                Assert.Contains("안녕하세요. IGNORE=1을 검토해 보세요.", output);
                Assert.Contains("contact.md", output);
                Assert.Contains("LS-DYNA 접촉 관통 문의", llm.Requests[1].Messages[0].Content);
            }
        }

        [Fact]
        public void Program_UnknownCommand_Returns2()
        {
            var (code, output) = Run(FakeEnv(), "frobnicate");
            Assert.Equal(2, code);
            Assert.Contains("사용법", output);
            Assert.Equal(1, Run(FakeEnv(), "index", "--root", @"C:\no\such\kb-root", "--model", @"C:\no\model").Code);
        }

        [Fact]
        public void Index_ReportsUnsupportedFormats_AndSkipsUnchangedPublish()
        {
            using (var tmp = new TempDir())
            {
                var root = Path.Combine(tmp.Root, "kb");
                Run(FakeEnv(), "init-kb", "--root", root);
                tmp.File("kb/LS-DYNA/faq/a.md", "접촉");
                tmp.File("kb/LS-DYNA/manuals/old.hwp", "x");
                var args = new[] { "index", "--root", root, "--product", "ls-dyna", "--model", FakeModel(tmp), "--work", Path.Combine(tmp.Root, "work") };
                var first = Run(FakeEnv(), args);
                Assert.Contains(".hwp 1개", first.Output);
                var second = Run(FakeEnv(), args);
                Assert.Equal(0, second.Code);
                Assert.Contains("변경 없음", second.Output);
                Assert.Equal(1, IndexManifest.Load(KbLayout.ManifestPath(root)).Find("ls-dyna").Version);
            }
        }
    }
}
