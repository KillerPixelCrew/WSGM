using System.Text.Json.Serialization;

namespace WSGM.Shell;

/// <summary>The hook state on the wire, camelCase as the fragment reads it.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WsgmControllerCapsState))]
internal sealed partial class WsgmControllerCapsJsonContext : JsonSerializerContext;
