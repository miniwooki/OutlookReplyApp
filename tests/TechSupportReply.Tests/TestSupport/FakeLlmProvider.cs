using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;

namespace TechSupportReply.Tests.TestSupport
{
    internal sealed class FakeLlmProvider : ILlmProvider
    {
        private readonly Queue<string> _responses = new Queue<string>();
        private readonly object _lock = new object();

        public string DisplayName => "Fake";
        public List<LlmRequest> Requests { get; } = new List<LlmRequest>();
        public Exception ThrowOnCall { get; set; }
        public TimeSpan Delay { get; set; }
        /// <summary>각 호출이 받은 취소 토큰(호출 순서).</summary>
        public List<CancellationToken> Tokens { get; } = new List<CancellationToken>();

        public FakeLlmProvider Enqueue(string response)
        {
            lock (_lock) _responses.Enqueue(response);
            return this;
        }

        public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            // 호출 시점에 지연·응답을 확정한다(이후 설정 변경이 이미 시작된 호출에 영향을 주지 않도록).
            var delay = Delay;
            string response;
            lock (_lock)
            {
                Requests.Add(request);
                Tokens.Add(ct);
                response = _responses.Count > 0 ? _responses.Dequeue() : "";
            }
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            if (ThrowOnCall != null) throw ThrowOnCall;
            return response;
        }

        public async Task<string> StreamAsync(LlmRequest request, Action<string> onDelta, CancellationToken ct)
        {
            var text = await CompleteAsync(request, ct);
            for (int i = 0; i < text.Length; i += 5)
            {
                ct.ThrowIfCancellationRequested();
                onDelta?.Invoke(text.Substring(i, Math.Min(5, text.Length - i)));
            }
            return text;
        }
    }
}
