using System.Text.Json.Serialization;

namespace WSGM.Device.Contracts.Packaging;

/// <summary>
/// Source-generated serialization for every manifest type.
/// </summary>
/// <remarks>
/// These contracts are compiled into WSGM's NativeAOT image, so reflection-based serialization is not
/// an option — the AOT publish is the compatibility proof and would fail. Everything here is
/// generated at build time.
/// <para>
/// <see cref="JsonUnmappedMemberHandling.Disallow"/> is the load-bearing setting: a member this
/// schema version does not define is an error rather than something silently dropped. A package
/// written against a newer schema must be rejected with <c>UnsupportedSchemaVersion</c>, not quietly
/// half-understood, because the fields we would ignore are exactly the ones carrying the new rules.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(PluginManifest))]
public sealed partial class DeviceContractsJsonContext : JsonSerializerContext;
