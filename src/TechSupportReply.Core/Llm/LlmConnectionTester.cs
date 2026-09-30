using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TechSupportReply.Core.Llm
{
    public sealed class ConnectionTestResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>설정 화면의 [연결 테스트]. 짧은 요청을 보내 키·모델·엔드포인트를 확인한다.</summary>
    public static class LlmConnectionTester
    {
        public static async Task<ConnectionTestResult> TestAsync(ILlmProvider llm, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (llm == null) throw new ArgumentNullException(nameof(llm));
            var request = new LlmRequest { CachedSystem = "연결 테스트입니다. 요청한 단어만 답하세요.", MaxTokens = 2000, Effort = "low" };
            request.Messages.Add(LlmMessage.User("OK라고만 답하세요."));
            var sw = Stopwatch.StartNew();
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
                try
                {
                    var text = (await llm.CompleteAsync(request, cts.Token).ConfigureAwait(false) ?? "").Trim();
                    if (text.Length > 40) text = text.Substring(0, 40) + "…";
                    return new ConnectionTestResult { Success = true, Message = $"연결 성공 ({sw.ElapsedMilliseconds} ms): {text}" };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new ConnectionTestResult { Message = "응답 시간이 초과되었습니다. 네트워크 또는 Base URL을 확인하세요." };
                }
                catch (LlmException ex)
                {
                    return new ConnectionTestResult { Message = ex.UserMessage };
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    return new ConnectionTestResult { Message = "연결 실패: " + ex.Message };
                }
            }
        }
    }
}
