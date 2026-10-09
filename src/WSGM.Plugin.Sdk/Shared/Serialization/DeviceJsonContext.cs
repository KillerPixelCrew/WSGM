using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Packaging;

namespace WSGM.Device.Sdk.Serialization;

/// <summary>JSON metadata for device package manifests.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(GlyphProfileManifest))]
public sealed partial class DeviceJsonContext : JsonSerializerContext;
