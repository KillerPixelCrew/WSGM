using System.Text.Json.Serialization;

namespace WSGM.Shell;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamThemesState))]
internal sealed partial class ThemesJsonContext : JsonSerializerContext;
