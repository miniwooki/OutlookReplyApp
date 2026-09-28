using System;
using System.IO;

namespace TechSupportReply.Indexer
{
    /// <summary>진행 메시지를 출력한다. 파일이 많을 때 출력이 넘치지 않도록 every개마다 한 줄만 쓴다.</summary>
    internal sealed class ConsoleProgress : IProgress<string>
    {
        private readonly TextWriter _output;
        private readonly int _every;
        private int _count;

        public ConsoleProgress(TextWriter output, int every = 20)
        {
            _output = output;
            _every = Math.Max(1, every);
        }

        public void Report(string value)
        {
            if (_count++ % _every == 0) _output.WriteLine("  " + value);
        }
    }
}
