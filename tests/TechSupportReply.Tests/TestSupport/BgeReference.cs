using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TechSupportReply.Tests.TestSupport
{
    internal sealed class ReferenceRecord
    {
        public string Text { get; set; }
        public int[] Ids { get; set; }
        public float[] Embedding { get; set; }
    }

    internal sealed class ReferenceFile
    {
        public string Model { get; set; }
        public List<ReferenceRecord> Records { get; set; }
    }

    internal static class BgeReference
    {
        public static ReferenceFile Load() =>
            JsonSerializer.Deserialize<ReferenceFile>(
                File.ReadAllText(TestPaths.Fixture("bge_m3_reference.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
