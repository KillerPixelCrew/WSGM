using System.Text.Json.Serialization;

namespace WSGM.Shell;

/// <summary>The page model on the wire, camelCase as the renderer reads it, and the plugin values it converts.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WsgmSteamSettingsState))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(double))]
internal sealed partial class WsgmSteamSettingsJsonContext : JsonSerializerContext;
