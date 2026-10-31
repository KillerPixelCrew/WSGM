using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Serializer metadata for the library's catalog.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<AnimationListing>))]
internal sealed partial class AnimationJsonContext : JsonSerializerContext;
