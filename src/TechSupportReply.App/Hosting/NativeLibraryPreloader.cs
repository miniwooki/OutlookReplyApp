using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TechSupportReply.App.Hosting
{
    /// <summary>
    /// Outlook 프로세스에서 System32의 onnxruntime.dll(Windows 내장 구버전)이 먼저 로드되지 않도록
    /// 애드인 폴더의 네이티브 DLL을 전체 경로로 먼저 로드한다. 같은 모듈 이름이 이미 로드되어 있으면 이후 DllImport는 그것을 쓴다.
    /// </summary>
    public static class NativeLibraryPreloader
    {
        public static readonly string[] Libraries = { "onnxruntime.dll", "e_sqlite3.dll" };
        private const uint LoadWithAlteredSearchPath = 0x00000008;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileName(IntPtr module, StringBuilder fileName, uint size);

        public static IReadOnlyList<string> Preload(string directory)
        {
            var warnings = new List<string>();
            foreach (var lib in Libraries)
            {
                var path = Find(directory, lib);
                if (path == null)
                {
                    warnings.Add($"{lib}을(를) 찾을 수 없습니다: {directory}");
                    continue;
                }
                if (LoadLibraryEx(path, IntPtr.Zero, LoadWithAlteredSearchPath) == IntPtr.Zero)
                    warnings.Add($"{lib} 로드 실패(Win32 오류 {Marshal.GetLastWin32Error()}): {path}");
            }
            return warnings;
        }

        /// <summary>현재 프로세스에 로드된 모듈의 전체 경로. 로드되지 않았으면 null.</summary>
        public static string LoadedModulePath(string moduleName)
        {
            var handle = GetModuleHandle(moduleName);
            if (handle == IntPtr.Zero) return null;
            var sb = new StringBuilder(1024);
            return GetModuleFileName(handle, sb, (uint)sb.Capacity) == 0 ? null : sb.ToString();
        }

        /// <summary>섀도 복사와 무관하게 어셈블리 원본 폴더를 돌려준다(VSTO는 CodeBase 폴더에 네이티브 DLL이 있다).</summary>
        public static string AssemblyDirectory(Assembly assembly) =>
            Path.GetDirectoryName(new Uri(assembly.CodeBase).LocalPath);

        private static string Find(string directory, string lib)
        {
            if (string.IsNullOrEmpty(directory)) return null;
            foreach (var candidate in new[] { Path.Combine(directory, lib), Path.Combine(directory, "runtimes", "win-x64", "native", lib) })
                if (File.Exists(candidate)) return candidate;
            return null;
        }
    }
}
