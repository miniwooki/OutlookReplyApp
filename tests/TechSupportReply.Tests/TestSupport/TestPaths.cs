using System;
using System.IO;
using System.Linq;

namespace TechSupportReply.Tests.TestSupport
{
    internal static class TestPaths
    {
        public const string ModelMissingMessage = "임베딩 모델이 없습니다. tools/download_model.sh를 먼저 실행하세요.";

        public static string RepoRoot { get; } = FindRepoRoot();

        public static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

        public static string Fixture(params string[] parts) =>
            Path.Combine(new[] { FixturesDir }.Concat(parts).ToArray());

        public static string ModelDir =>
            Environment.GetEnvironmentVariable("TSR_MODEL_DIR")
            ?? Path.Combine(RepoRoot, "models", "bge-m3-int8");

        public static bool ModelAvailable =>
            File.Exists(Path.Combine(ModelDir, "model.onnx"))
            && File.Exists(Path.Combine(ModelDir, "sentencepiece.bpe.model"));

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TechSupportReply.sln")))
                dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("TechSupportReply.sln을 찾을 수 없습니다.");
            return dir.FullName;
        }
    }
}
