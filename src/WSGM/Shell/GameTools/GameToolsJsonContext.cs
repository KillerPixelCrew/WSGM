using System.Text.Json.Serialization;

namespace WSGM.Shell;

/// <summary>Source-generated camel-case serialization of the Game Tools state.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GameToolsState))]
internal sealed partial class GameToolsJsonContext : JsonSerializerContext;
