using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TechSupportReply.Core.IO;

namespace TechSupportReply.Core.Settings
{
    /// <summary>API 키를 현재 Windows 사용자 DPAPI로 암호화해 저장한다.</summary>
    public sealed class SecretStore
    {
        public const string FileName = "secrets.dat";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TechSupportReply.secrets.v1");
        private readonly string _path;
        private readonly object _lock = new object();

        public SecretStore(string directory)
        {
            _path = Path.Combine(directory, FileName);
        }

        public static string NewId() => Guid.NewGuid().ToString("N");

        public string Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock)
            {
                return ReadAll(throwOnCorrupt: true).TryGetValue(id, out var value) ? value : null;
            }
        }

        public void Set(string id, string value)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("비밀 id가 비어 있습니다.", nameof(id));
            lock (_lock)
            {
                var all = ReadAll(throwOnCorrupt: false);
                all[id] = value;
                WriteAll(all);
            }
        }

        public void Remove(string id)
        {
            lock (_lock)
            {
                var all = ReadAll(throwOnCorrupt: false);
                if (all.Remove(id)) WriteAll(all);
            }
        }

        private Dictionary<string, string> ReadAll(bool throwOnCorrupt)
        {
            if (!File.Exists(_path)) return new Dictionary<string, string>();
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain))
                       ?? new Dictionary<string, string>();
            }
            catch (Exception ex) when (ex is CryptographicException || ex is JsonException)
            {
                if (!throwOnCorrupt) return new Dictionary<string, string>();
                throw new InvalidOperationException(
                    "저장된 API 키를 복호화할 수 없습니다. 다른 Windows 사용자 계정에서 만든 파일이거나 손상되었습니다. 설정에서 키를 다시 등록하세요.", ex);
            }
        }

        private void WriteAll(Dictionary<string, string> all)
        {
            var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all));
            AtomicFile.WriteAllBytes(_path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
    }
}
