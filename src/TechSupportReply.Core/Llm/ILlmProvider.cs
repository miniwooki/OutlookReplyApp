using System;
using System.Threading;
using System.Threading.Tasks;

namespace TechSupportReply.Core.Llm
{
    public interface ILlmProvider
    {
        string DisplayName { get; }

        Task<string> CompleteAsync(LlmRequest request, CancellationToken ct);

        /// <summary>텍스트 조각이 올 때마다 onDelta를 호출하고, 완료되면 전체 텍스트를 반환한다.</summary>
        Task<string> StreamAsync(LlmRequest request, Action<string> onDelta, CancellationToken ct);
    }
}
