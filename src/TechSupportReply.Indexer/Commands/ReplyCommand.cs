using System;
using System.IO;
using System.Linq;
using System.Threading;
using TechSupportReply.Core.Generation;
using TechSupportReply.Core.Models;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Loaders;

namespace TechSupportReply.Indexer.Commands
{
    /// <summary>Outlook 없이 메일 파일로 분류 → 검색 → 답변 생성 전 과정을 확인하는 개발용 명령. 발송하지 않는다.</summary>
    internal static class ReplyCommand
    {
        public static int Run(CliArgs args, TextWriter output, IndexerEnvironment env, CancellationToken ct)
        {
            var mail = ReadMail(args.Require("mail"));
            var profile = BuildProfile(args);
            var keyName = profile.Provider == LlmProviderKind.Anthropic ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY";
            var key = env.GetEnv(keyName);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException($"환경 변수 {keyName}에 API 키를 설정하세요.");
            var llm = env.CreateLlm(profile, key);

            using (var session = new RetrievalSession(args, env, ct))
            {
                var productId = args.Get("product");
                if (productId == null)
                {
                    var c = new ProductClassifier(session.Catalog).ClassifyAsync(mail, llm, ct).GetAwaiter().GetResult();
                    productId = c.ProductId;
                    output.WriteLine($"제품군: {c.ProductId} ({session.Catalog.Find(c.ProductId)?.DisplayName}) 신뢰도 {c.Confidence:0.00} [{c.Source}] — {c.Reason}");
                }
                else
                {
                    output.WriteLine($"제품군(지정): {productId}");
                }

                var generator = new ReplyGenerator(session.Catalog, session.Retriever, session.LoadGuide, new AppSettings());
                output.WriteLine("== 답변 ==");
                var result = generator.GenerateAsync(new ReplyRequest
                {
                    Mail = mail,
                    ProductId = productId,
                    ExtraInstruction = args.Get("instruction", ""),
                    UseRag = !args.Has("no-rag"),
                }, llm, output.Write, ct).GetAwaiter().GetResult();
                output.WriteLine();
                SearchCommand.Print(output, "참고 문서", result.References);
                SearchCommand.PrintWarnings(output, result.Warnings);
            }
            return 0;
        }

        private static LlmProfile BuildProfile(CliArgs args)
        {
            var provider = (args.Get("provider", "anthropic")).ToLowerInvariant();
            if (provider != "anthropic" && provider != "openai") throw new CliException("--provider는 anthropic 또는 openai입니다.");
            var kind = provider == "anthropic" ? LlmProviderKind.Anthropic : LlmProviderKind.OpenAI;
            return new LlmProfile
            {
                DisplayName = "CLI " + provider,
                Provider = kind,
                Model = kind == LlmProviderKind.Anthropic ? args.Get("model-name", "claude-opus-5") : args.Get("model-name") ?? throw new CliException("OpenAI는 --model-name이 필요합니다."),
                BaseUrl = args.Get("base-url", ""),
                Effort = args.Get("effort", "medium"),
            };
        }

        /// <summary>.eml/.msg는 메일 파일로, .txt는 첫 줄을 제목·나머지를 본문으로 읽는다.</summary>
        internal static MailSnapshot ReadMail(string path)
        {
            if (!File.Exists(path)) throw new InvalidOperationException($"메일 파일이 없습니다: {path}");
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".eml" || ext == ".msg")
            {
                var m = MailFileReader.Read(path);
                return new MailSnapshot { Subject = m.Subject, Body = m.Body, SenderName = m.From, ReceivedAt = m.Date ?? DateTime.Now };
            }
            var lines = TextFileReader.ReadAllText(path).Replace("\r\n", "\n").Split('\n').ToList();
            var first = lines.FindIndex(l => l.Trim().Length > 0);
            if (first < 0) throw new InvalidOperationException("메일 파일이 비어 있습니다.");
            return new MailSnapshot
            {
                Subject = lines[first].Trim(),
                Body = string.Join("\n", lines.Skip(first + 1)).Trim(),
                ReceivedAt = File.GetLastWriteTime(path),
            };
        }
    }
}
