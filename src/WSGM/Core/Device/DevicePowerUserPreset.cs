namespace WSGM.Core;

/// <summary>A power preset the user saved from a Custom reading, bound to the device family it was saved on.</summary>
public sealed class DevicePowerUserPreset
{
    /// <summary>Device family the preset belongs to, the same identity as <see cref="DevicePowerPresetReference.PluginId" />.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>Stable identifier, always beginning with <see cref="DevicePowerUserPresets.IdPrefix" />.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The name the user gave it; unique per device family, ignoring case.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The complete power state it applies, the same values a Custom assignment keeps.</summary>
    public DevicePowerCustomValues? Values { get; set; }
}
