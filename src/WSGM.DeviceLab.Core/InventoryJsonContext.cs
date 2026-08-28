using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Evidence;
using WSGM.DeviceLab.Core.Fixtures;
using WSGM.DeviceLab.Core.Inventory;
using WSGM.DeviceLab.Core.Preflight;
using WSGM.DeviceLab.Core.Probes;
using WSGM.DeviceLab.Core.Scaffolding;

namespace WSGM.DeviceLab.Core;

/// <summary>
/// Serialization for Device Lab output.
/// </summary>
/// <remarks>
/// Source-generated even though this assembly is JIT. The reason is determinism rather than AOT: a
/// generated context emits properties in declaration order, so two runs over the same machine produce
/// byte-identical output and a capture can be diffed or hashed meaningfully.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    WriteIndented = true)]
[JsonSerializable(typeof(MachineInventory))]
[JsonSerializable(typeof(PrivateCaptureManifest))]
[JsonSerializable(typeof(ShareableCaptureManifest))]
[JsonSerializable(typeof(ObserveOnlyRecipe))]
[JsonSerializable(typeof(CaptureStreamEvent))]
[JsonSerializable(typeof(CaptureAnalysisResult))]
[JsonSerializable(typeof(CaptureRedactionManifest))]
[JsonSerializable(typeof(EvidenceClaim[]))]
[JsonSerializable(typeof(EvidenceLock))]
[JsonSerializable(typeof(FixtureManifest))]
[JsonSerializable(typeof(ScaffoldInputManifest))]
[JsonSerializable(typeof(ScaffoldOutputManifest))]
[JsonSerializable(typeof(DeviceLabDoctorReport))]
[JsonSerializable(typeof(ReadProbeHostRequest))]
[JsonSerializable(typeof(ReadProbeHostResponse))]
[JsonSerializable(typeof(RecoveryJournalEntry))]
public sealed partial class DeviceLabJsonContext : JsonSerializerContext;

/// <summary>Compact source-generated serialization for newline-delimited capture streams.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    WriteIndented = false)]
[JsonSerializable(typeof(CaptureStreamEvent))]
[JsonSerializable(typeof(CaptureAnalysisResult))]
public sealed partial class DeviceLabCompactJsonContext : JsonSerializerContext;

/// <summary>Writes Device Lab results in their canonical form.</summary>
public static class DeviceLabJson
{
    /// <summary>
    /// Serializes an inventory to its canonical JSON form.
    /// </summary>
    /// <param name="inventory">The inventory to write.</param>
    /// <returns>Indented JSON, stable across runs for the same input.</returns>
    public static string Serialize(MachineInventory inventory) =>
        JsonSerializer.Serialize(inventory, DeviceLabJsonContext.Default.MachineInventory);

    /// <summary>Serializes a shareable capture manifest to canonical JSON.</summary>
    /// <param name="manifest">Manifest to serialize.</param>
    /// <returns>Indented JSON with deterministic property ordering.</returns>
    public static string Serialize(ShareableCaptureManifest manifest) =>
        JsonSerializer.Serialize(manifest, DeviceLabJsonContext.Default.ShareableCaptureManifest);

    /// <summary>Serializes an inert observe-only recipe to canonical JSON.</summary>
    /// <param name="recipe">Recipe to serialize.</param>
    /// <returns>Indented JSON with deterministic property ordering.</returns>
    public static string Serialize(ObserveOnlyRecipe recipe) =>
        JsonSerializer.Serialize(recipe, DeviceLabJsonContext.Default.ObserveOnlyRecipe);

    /// <summary>Serializes a simulator-only fixture manifest to canonical JSON.</summary>
    /// <param name="manifest">Manifest to serialize.</param>
    /// <returns>Indented JSON with deterministic property ordering.</returns>
    public static string Serialize(FixtureManifest manifest) =>
        JsonSerializer.Serialize(manifest, DeviceLabJsonContext.Default.FixtureManifest);

    /// <summary>Serializes frozen scaffold inputs to canonical JSON.</summary>
    /// <param name="input">Input manifest to serialize.</param>
    /// <returns>Indented JSON with deterministic property ordering.</returns>
    public static string Serialize(ScaffoldInputManifest input) =>
        JsonSerializer.Serialize(input, DeviceLabJsonContext.Default.ScaffoldInputManifest);

    /// <summary>Serializes scaffold outputs to canonical JSON.</summary>
    /// <param name="output">Output manifest to serialize.</param>
    /// <returns>Indented JSON with deterministic property ordering.</returns>
    public static string Serialize(ScaffoldOutputManifest output) =>
        JsonSerializer.Serialize(output, DeviceLabJsonContext.Default.ScaffoldOutputManifest);

    /// <summary>Serializes a deterministic evidence lock.</summary>
    /// <param name="evidenceLock">Evidence lock to serialize.</param>
    /// <returns>Indented canonical JSON.</returns>
    public static string Serialize(EvidenceLock evidenceLock) =>
        JsonSerializer.Serialize(evidenceLock, DeviceLabJsonContext.Default.EvidenceLock);

    /// <summary>Serializes a Device Lab doctor report to canonical JSON.</summary>
    /// <param name="report">Doctor report to serialize.</param>
    /// <returns>Indented JSON with deterministic property ordering.</returns>
    public static string Serialize(DeviceLabDoctorReport report) =>
        JsonSerializer.Serialize(report, DeviceLabJsonContext.Default.DeviceLabDoctorReport);

    /// <summary>Serializes one inert ProbeHost invocation envelope.</summary>
    /// <param name="request">Request to serialize.</param>
    /// <returns>Indented deterministic JSON.</returns>
    public static string Serialize(ReadProbeHostRequest request) =>
        JsonSerializer.Serialize(request, DeviceLabJsonContext.Default.ReadProbeHostRequest);

    /// <summary>Serializes one ProbeHost response.</summary>
    /// <param name="response">Response to serialize.</param>
    /// <returns>Indented deterministic JSON.</returns>
    public static string Serialize(ReadProbeHostResponse response) =>
        JsonSerializer.Serialize(response, DeviceLabJsonContext.Default.ReadProbeHostResponse);
}
