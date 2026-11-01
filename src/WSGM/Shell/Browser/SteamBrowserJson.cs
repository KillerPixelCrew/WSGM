using System.Text.Json.Serialization;

namespace WSGM.Shell;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamBrowserState))]
internal partial class SteamBrowserJson : JsonSerializerContext;
