using System;
using System.Collections.Generic;
using System.Globalization;

namespace TechSupportReply.Indexer
{
    /// <summary>사용법 오류(종료 코드 2).</summary>
    internal sealed class CliException : Exception
    {
        public CliException(string message) : base(message)
        {
        }
    }

    /// <summary>`명령 --이름 값 --플래그 --이름=값` 형식의 인수 파서.</summary>
    internal sealed class CliArgs
    {
        private readonly Dictionary<string, string> _options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Command { get; private set; } = "";

        public static CliArgs Parse(string[] args)
        {
            var result = new CliArgs();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--"))
                {
                    if (result.Command.Length == 0) result.Command = arg.ToLowerInvariant();
                    else throw new CliException($"알 수 없는 인수: {arg}");
                    continue;
                }
                var name = arg.Substring(2);
                int eq = name.IndexOf('=');
                if (eq >= 0) result._options[name.Substring(0, eq)] = name.Substring(eq + 1);
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) result._options[name] = args[++i];
                else result._flags.Add(name);
            }
            return result;
        }

        public string Get(string name, string defaultValue = null) =>
            _options.TryGetValue(name, out var value) ? value : defaultValue;

        public string Require(string name) => Get(name) ?? throw new CliException($"--{name} 옵션이 필요합니다.");

        public bool Has(string flag) => _flags.Contains(flag);

        public int GetInt(string name, int defaultValue)
        {
            var value = Get(name);
            if (value == null) return defaultValue;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
                throw new CliException($"--{name} 값은 양의 정수여야 합니다: {value}");
            return n;
        }
    }
}
