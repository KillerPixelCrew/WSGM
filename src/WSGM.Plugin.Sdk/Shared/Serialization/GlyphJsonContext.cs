using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Device.Sdk.Serialization;

/// <summary>JSON metadata for physical glyph profile manifests.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(GlyphProfileManifest))]
public sealed partial class GlyphJsonContext : JsonSerializerContext;
