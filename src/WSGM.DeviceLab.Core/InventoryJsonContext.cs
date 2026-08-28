using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.DeviceLab.Core.Inventory;

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
    WriteIndented = true)]
[JsonSerializable(typeof(MachineInventory))]
public sealed partial class DeviceLabJsonContext : JsonSerializerContext;

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
}
