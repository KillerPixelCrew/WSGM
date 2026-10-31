using System.Text.Json.Serialization;

namespace WSGM.Shell;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamArtworkBrowserState))]
internal sealed partial class ArtworkJsonContext : JsonSerializerContext;
