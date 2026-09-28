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

        public string DisplayName => "Fake";
        public List<LlmRequest> Requests { get; } = new List<LlmRequest>();
        public Exception ThrowOnCall { get; set; }
        public TimeSpan Delay { get; set; }

        public FakeLlmProvider Enqueue(string response)
        {
            _responses.Enqueue(response);
            return this;
        }

        public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (ThrowOnCall != null) throw ThrowOnCall;
            return _responses.Count > 0 ? _responses.Dequeue() : "";
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
