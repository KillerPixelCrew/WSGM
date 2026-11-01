using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Source-generated metadata for the update state file.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateState))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
