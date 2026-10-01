using System;
using System.IO;
using TechSupportReply.Core.Diagnostics;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Diagnostics
{
    public class FileLogTests
    {
        [Fact]
        public void WritesToDailyFile_WithLevelAndException()
        {
            using (var tmp = new TempDir())
            {
                var log = new FileLog(tmp.Root, () => new DateTime(2026, 9, 28, 13, 5, 7));
                log.Info("시작");
                log.Error("실패", new InvalidOperationException("boom"));

                var path = Path.Combine(tmp.Root, "2026-09-28.log");
                Assert.Equal(path, log.CurrentPath);
                var text = File.ReadAllText(path);
                Assert.Contains("2026-09-28 13:05:07.000 INFO 시작", text);
                Assert.Contains("ERROR 실패 | System.InvalidOperationException: boom", text);
            }
        }

        [Fact]
        public void Error_IncludesInnerExceptionChain()
        {
            using (var tmp = new TempDir())
            {
                var log = new FileLog(tmp.Root, () => new DateTime(2026, 10, 1, 14, 14, 24));
                var inner = new IOException("Unable to read data from the transport connection",
                    new System.Net.Sockets.SocketException(10054));
                log.Error("모델 목록 불러오기 실패", new AggregateException("Retry failed after 4 tries.", inner));

                var text = File.ReadAllText(Path.Combine(tmp.Root, "2026-10-01.log"));
                Assert.Contains("System.AggregateException", text);
                Assert.Contains("System.IO.IOException: Unable to read data from the transport connection", text);
                Assert.Contains("System.Net.Sockets.SocketException", text);
            }
        }

        [Fact]
        public void NeverThrows_WhenDirectoryIsInvalid()
        {
            using (var tmp = new TempDir())
            {
                var fileInsteadOfDir = tmp.File("not-a-dir", "x");
                var log = new FileLog(fileInsteadOfDir);

                var ex = Record.Exception(() =>
                {
                    log.Info("무시되어야 함");
                    log.Warn("무시되어야 함");
                    log.Error("무시되어야 함", new InvalidOperationException("boom"));
                    log.Cleanup();
                });

                Assert.Null(ex);
                // 잘못된 경로는 그대로 남고(파일 내용 불변), 로그 파일은 만들어지지 않는다.
                Assert.True(File.Exists(fileInsteadOfDir));
                Assert.Equal("x", File.ReadAllText(fileInsteadOfDir));
                Assert.Single(Directory.GetFileSystemEntries(tmp.Root));
            }
        }

        [Fact]
        public void Cleanup_DeletesOnlyOldLogFiles()
        {
            using (var tmp = new TempDir())
            {
                var old = tmp.File("2026-08-01.log", "x");
                var recent = tmp.File("2026-09-20.log", "x");
                var other = tmp.File("notes.log", "x");
                new FileLog(tmp.Root, () => new DateTime(2026, 9, 28)).Cleanup(keepDays: 30);
                Assert.False(File.Exists(old));
                Assert.True(File.Exists(recent));
                Assert.True(File.Exists(other));
            }
        }
    }
}
