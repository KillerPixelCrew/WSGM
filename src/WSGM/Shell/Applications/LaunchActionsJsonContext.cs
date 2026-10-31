using System.Text.Json.Serialization;

namespace WSGM.Shell;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LaunchActionState))]
internal sealed partial class LaunchActionsJsonContext : JsonSerializerContext;
