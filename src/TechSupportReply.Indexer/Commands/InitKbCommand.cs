using System.IO;
using System.Text;
using TechSupportReply.Core.Products;
using TechSupportReply.Rag.Indexing;

namespace TechSupportReply.Indexer.Commands
{
    /// <summary>공유 지식 폴더의 기본 구조와 템플릿을 만든다. 이미 있는 파일은 건드리지 않는다.</summary>
    internal static class InitKbCommand
    {
        public static int Run(CliArgs args, TextWriter output)
        {
            var root = args.Require("root");
            Directory.CreateDirectory(root);
            var productsPath = KbLayout.ProductsPath(root);
            if (!File.Exists(productsPath))
            {
                ProductCatalog.CreateDefault().Save(productsPath);
                output.WriteLine($"생성: {productsPath}");
            }
            var catalog = ProductCatalog.Load(productsPath);
            foreach (var product in catalog.Products)
            {
                var dir = KbLayout.ProductDir(root, product);
                foreach (var sub in KbTemplates.ProductSubfolders) Directory.CreateDirectory(Path.Combine(dir, sub));
                WriteIfMissing(KbLayout.PromptPath(root, product), KbTemplates.Prompt(product), output);
            }
            Directory.CreateDirectory(KbLayout.IndexDir(root));
            Directory.CreateDirectory(Path.Combine(root, KbLayout.ModelsFolder));
            WriteIfMissing(Path.Combine(root, "README.md"), KbTemplates.Readme, output);
            output.WriteLine($"지식 폴더 구조를 준비했습니다: {root}");
            return 0;
        }

        private static void WriteIfMissing(string path, string content, TextWriter output)
        {
            if (File.Exists(path)) return;
            File.WriteAllText(path, content, new UTF8Encoding(false));
            output.WriteLine($"생성: {path}");
        }
    }
}
