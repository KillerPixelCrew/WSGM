using System.Text.Json.Serialization;

namespace WSGM.Device.Contracts.Glyphs;

/// <summary>NativeAOT-safe JSON metadata for physical glyph profiles.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(GlyphProfileManifest))]
public sealed partial class GlyphProfileJsonContext : JsonSerializerContext;
