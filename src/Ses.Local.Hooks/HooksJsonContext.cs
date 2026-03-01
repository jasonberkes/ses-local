using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ses.Local.Hooks;

[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class HooksJsonContext : JsonSerializerContext;
