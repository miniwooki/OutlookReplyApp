using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Knowledge;

namespace TechSupportReply.Tests.TestSupport
{
    internal sealed class FakeRetriever : IKnowledgeRetriever
    {
        public RetrievalResult Result { get; set; } = new RetrievalResult();
        public Exception Throw { get; set; }
        public List<(string ProductId, string Query)> Calls { get; } = new List<(string, string)>();

        public Task<RetrievalResult> RetrieveAsync(string productId, string query, int referenceTopK, int styleTopK, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((productId, query));
            if (Throw != null) throw Throw;
            return Task.FromResult(Result);
        }
    }
}
