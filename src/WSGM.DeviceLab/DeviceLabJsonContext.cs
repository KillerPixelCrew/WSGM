using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Fixtures;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Preflight;
using WSGM.DeviceLab.Probes;

namespace WSGM.DeviceLab;

/// <summary>
///     Serialization for Device Lab output.
/// </summary>
/// <remarks>
///     The single assembly context keeps property order deterministic so identical observations produce
///     byte-identical output and capture hashes remain meaningful.
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
[JsonSerializable(typeof(FixtureManifest))]
[JsonSerializable(typeof(DeviceLabDoctorReport))]
[JsonSerializable(typeof(ReadProbeWorkerRequest))]
[JsonSerializable(typeof(ReadProbeWorkerResponse))]
internal sealed partial class DeviceLabJsonContext : JsonSerializerContext;

internal static class DeviceLabCompactJson
{
    private static readonly DeviceLabJsonContext Context = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        WriteIndented = false
    });

    internal static JsonTypeInfo<CaptureStreamEvent> CaptureStreamEvent => Context.CaptureStreamEvent;

    internal static JsonTypeInfo<CaptureAnalysisResult> CaptureAnalysisResult => Context.CaptureAnalysisResult;
}

/// <summary>Writes Device Lab results in their canonical form.</summary>
internal static class DeviceLabJson
{
    /// <summary>
    ///     Serializes an inventory to its canonical JSON form.
    /// </summary>
    /// <param name="inventory">The inventory to write.</param>
    /// <returns>Indented JSON, stable across runs for the same input.</returns>
    public static string Serialize(MachineInventory inventory)
    {
        return JsonSerializer.Serialize(inventory, DeviceLabJsonContext.Default.MachineInventory);
    }

    /// <summary>Serializes an inert observe-only recipe to canonical JSON.</summary>
    /// <param name="recipe">Recipe to serialize.</param>
    /// <returns>Indented JSON with deterministic property ordering.</returns>
    public static string Serialize(ObserveOnlyRecipe recipe)
    {
        return JsonSerializer.Serialize(recipe, DeviceLabJsonContext.Default.ObserveOnlyRecipe);
    }

    /// <summary>Serializes a Device Lab doctor report to canonical JSON.</summary>
    /// <param name="report">Doctor report to serialize.</param>
    /// <returns>Indented JSON with deterministic property ordering.</returns>
    public static string Serialize(DeviceLabDoctorReport report)
    {
        return JsonSerializer.Serialize(report, DeviceLabJsonContext.Default.DeviceLabDoctorReport);
    }

    /// <summary>Serializes one inert read-probe worker invocation envelope.</summary>
    /// <param name="request">Request to serialize.</param>
    /// <returns>Indented deterministic JSON.</returns>
    public static string Serialize(ReadProbeWorkerRequest request)
    {
        return JsonSerializer.Serialize(request, DeviceLabJsonContext.Default.ReadProbeWorkerRequest);
    }
}
