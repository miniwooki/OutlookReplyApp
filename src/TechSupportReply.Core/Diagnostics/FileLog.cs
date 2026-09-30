using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TechSupportReply.Core.Diagnostics
{
    /// <summary>
    /// %LOCALAPPDATA%\TechSupportReply\logs\yyyy-MM-dd.log에 한 줄씩 기록한다. 메일 본문과 API 키는 넣지 않는다.
    /// 로깅 실패가 Outlook 동작을 막으면 안 되므로 어떤 메서드도 예외를 던지지 않는다.
    /// </summary>
    public sealed class FileLog
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly string _dir;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();

        public FileLog(string directory, Func<DateTime> clock = null)
        {
            _dir = directory ?? DefaultDirectory;
            _clock = clock ?? (() => DateTime.Now);
        }

        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TechSupportReply", "logs");

        public string CurrentPath => PathFor(_clock());

        public void Info(string message) => Write("INFO", message, null);

        public void Warn(string message) => Write("WARN", message, null);

        public void Error(string message, Exception ex = null) => Write("ERROR", message, ex);

        public void Cleanup(int keepDays = 30)
        {
            try
            {
                if (!Directory.Exists(_dir)) return;
                var cutoff = _clock().Date.AddDays(-keepDays);
                foreach (var file in Directory.GetFiles(_dir, "????-??-??.log"))
                {
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                        && day < cutoff)
                        File.Delete(file);
                }
            }
            catch (Exception)
            {
                // 정리 실패는 무시한다.
            }
        }

        private string PathFor(DateTime time) =>
            Path.Combine(_dir, time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        private void Write(string level, string message, Exception ex)
        {
            try
            {
                var now = _clock();
                var line = new StringBuilder()
                    .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(level).Append(' ').Append(message ?? "");
                if (ex != null) line.Append(" | ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message).Append('\n').Append(ex.StackTrace);
                line.Append('\n');
                lock (_lock)
                {
                    Directory.CreateDirectory(_dir);
                    File.AppendAllText(PathFor(now), line.ToString(), Utf8);
                }
            }
            catch (Exception)
            {
                // 로그를 쓸 수 없어도 계속 진행한다.
            }
        }
    }
}
