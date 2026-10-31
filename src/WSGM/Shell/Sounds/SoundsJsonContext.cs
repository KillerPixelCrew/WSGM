using System.Text.Json.Serialization;

namespace WSGM.Shell;

/// <summary>Serializer metadata for the page's state.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamSoundsState))]
internal sealed partial class SoundsJsonContext : JsonSerializerContext;
