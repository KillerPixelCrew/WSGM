using System.Text.Json.Serialization;

namespace WSGM.Shell;

/// <summary>The page model on the wire, camelCase as the renderer reads it.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WsgmSteamSettingsState))]
internal sealed partial class WsgmSteamSettingsJsonContext : JsonSerializerContext;
