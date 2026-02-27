using System.Text.Json.Serialization;

namespace Ses.Local.Core.Models;

public sealed class UpdateManifest
{
    public string Version { get; set; } = string.Empty;
    public DateTime Published { get; set; }
    public Dictionary<string, string> Binaries { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateManifest))]
public partial class UpdateManifestJsonContext : JsonSerializerContext { }
