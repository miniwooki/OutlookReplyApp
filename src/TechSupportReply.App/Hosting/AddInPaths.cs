using TechSupportReply.Core.Diagnostics;
using TechSupportReply.Core.Settings;
using TechSupportReply.Rag.Sync;

namespace TechSupportReply.App.Hosting
{
    public sealed class AddInPaths
    {
        public string SettingsDirectory { get; set; } = SettingsStore.DefaultDirectory;
        public string CacheDirectory { get; set; } = IndexCacheSync.DefaultCacheDir;
        public string LogDirectory { get; set; } = FileLog.DefaultDirectory;
        /// <summary>onnxruntime.dll·e_sqlite3.dll이 있는 폴더. null이면 선로딩하지 않는다(테스트).</summary>
        public string NativeDirectory { get; set; }

        public static AddInPaths Default() => new AddInPaths
        {
            NativeDirectory = NativeLibraryPreloader.AssemblyDirectory(typeof(AddInPaths).Assembly),
        };
    }
}
