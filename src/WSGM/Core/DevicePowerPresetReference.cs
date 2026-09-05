namespace WSGM.Core;

/// <summary>A saved preset assignment bound to the device plugin that declared it.</summary>
public sealed record DevicePowerPresetReference
{
    /// <summary>Package ID, preventing a replacement plugin from inheriting another device's policy.</summary>
    public string PluginId { get; set; } = string.Empty;

    /// <summary>Stable preset ID declared by that plugin.</summary>
    public string PresetId { get; set; } = string.Empty;
}
