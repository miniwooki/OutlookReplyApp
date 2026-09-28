using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechSupportReply.Core.Serialization
{
    public static class JsonDefaults
    {
        public static readonly JsonSerializerOptions Options = Create();

        private static JsonSerializerOptions Create()
        {
            var o = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            o.Converters.Add(new JsonStringEnumConverter());
            return o;
        }
    }
}
