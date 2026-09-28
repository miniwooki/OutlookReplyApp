using System;
using System.IO;
using System.Text;
using System.Threading;
using TechSupportReply.Core.Llm;
using TechSupportReply.Indexer.Commands;

namespace TechSupportReply.Indexer
{
    internal static class Program
    {
        private const string Usage =
@"사용법: TechSupportReply.Indexer <명령> [옵션]

  init-kb --root <공유 폴더>
      지식 폴더 구조, products.json, 제품별 _prompt.md 템플릿을 만듭니다.
  index   --root <공유 폴더> [--product <id>] [--full] [--model <모델 폴더>] [--work <작업 폴더>]
      제품 폴더를 색인해 _index에 게시합니다(바뀐 파일만 다시 임베딩).
  search  --root <공유 폴더> --product <id> --query <텍스트> [--top 8] [--cache <폴더>]
      애드인과 같은 방식으로 캐시를 동기화하고 검색 결과를 보여 줍니다.
  reply   --root <공유 폴더> --mail <파일.txt|.eml|.msg> [--product <id>] [--provider anthropic|openai]
          [--model-name <모델>] [--base-url <url>] [--instruction <추가 지시>] [--no-rag]
      분류 → 검색 → 답변 생성을 콘솔에서 확인합니다(발송하지 않음).
      API 키는 환경 변수 ANTHROPIC_API_KEY 또는 OPENAI_API_KEY에서 읽습니다.";

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };
                return Run(args, Console.Out, new IndexerEnvironment(), cts.Token);
            }
        }

        internal static int Run(string[] args, TextWriter output, IndexerEnvironment env) =>
            Run(args, output, env, CancellationToken.None);

        internal static int Run(string[] args, TextWriter output, IndexerEnvironment env, CancellationToken ct)
        {
            try
            {
                var cli = CliArgs.Parse(args);
                switch (cli.Command)
                {
                    case "":
                    case "help":
                        output.WriteLine(Usage);
                        return 0;
                    case "init-kb": return InitKbCommand.Run(cli, output);
                    case "index": return IndexCommand.Run(cli, output, env, ct);
                    case "search": return SearchCommand.Run(cli, output, env, ct);
                    case "reply": return ReplyCommand.Run(cli, output, env, ct);
                    default:
                        throw new CliException($"알 수 없는 명령: {cli.Command}");
                }
            }
            catch (CliException ex)
            {
                output.WriteLine("오류: " + ex.Message);
                output.WriteLine();
                output.WriteLine(Usage);
                return 2;
            }
            catch (OperationCanceledException)
            {
                output.WriteLine("중지했습니다.");
                return 1;
            }
            catch (LlmException ex)
            {
                output.WriteLine("오류: " + ex.UserMessage);
                return 1;
            }
            catch (Exception ex)
            {
                output.WriteLine("오류: " + ex.Message);
                return 1;
            }
        }
    }
}
