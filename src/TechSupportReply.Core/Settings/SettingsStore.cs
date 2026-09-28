using System;
using System.IO;
using System.Text;
using System.Text.Json;
using TechSupportReply.Core.IO;
using TechSupportReply.Core.Serialization;

namespace TechSupportReply.Core.Settings
{
    public sealed class SettingsStore
    {
        public const string FileName = "settings.json";

        public SettingsStore(string directory)
        {
            FilePath = Path.Combine(directory, FileName);
        }

        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TechSupportReply");

        public string FilePath { get; }

        public AppSettings Load()
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8), JsonDefaults.Options)
                       ?? new AppSettings();
            }
            catch (JsonException)
            {
                File.Copy(FilePath, FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                return new AppSettings();
            }
        }

        public void Save(AppSettings settings) =>
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonDefaults.Options));
    }
}
