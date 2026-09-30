using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TechSupportReply.Tests.TestSupport
{
    /// <summary>준비한 응답을 차례로 돌려주고 요청(메서드·URI·헤더)을 기록하는 HTTP 처리기. 네트워크 없이 SDK 호출을 검증한다.</summary>
    internal sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly object _lock = new object();
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new Queue<(HttpStatusCode Status, string Body)>();

        public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();

        public StubHttpHandler Respond(HttpStatusCode status, string body)
        {
            lock (_lock) _responses.Enqueue((status, body));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            (HttpStatusCode Status, string Body) next;
            lock (_lock)
            {
                Requests.Add(new RecordedRequest(request));
                if (_responses.Count == 0) throw new InvalidOperationException("준비된 응답이 없습니다: " + request.RequestUri);
                next = _responses.Dequeue();
            }
            return Task.FromResult(new HttpResponseMessage(next.Status)
            {
                Content = new StringContent(next.Body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    /// <summary>SDK가 요청 객체를 해제해도 읽을 수 있도록 보낸 시점에 복사한 요청 정보.</summary>
    internal sealed class RecordedRequest
    {
        public RecordedRequest(HttpRequestMessage request)
        {
            Method = request.Method.Method;
            Uri = request.RequestUri;
            Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        }

        public string Method { get; }
        public Uri Uri { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }

        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }
}
