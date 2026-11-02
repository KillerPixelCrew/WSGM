namespace WSGM.Core;

/// <summary>What a change to the saved power presets does.</summary>
public enum DevicePowerUserPresetEditKind
{
    /// <summary>Adds a preset with a new identifier.</summary>
    Save,

    /// <summary>Renames an existing preset.</summary>
    Rename,

    /// <summary>Replaces an existing preset's values, keeping its identifier and name.</summary>
    Update,

    /// <summary>Removes an existing preset.</summary>
    Delete
}

/// <summary>One change to the saved power presets of a device family.</summary>
/// <param name="Kind">What the change does.</param>
/// <param name="PluginId">The device family whose presets change.</param>
/// <param name="Id">The new preset's identifier for a save; the preset changed otherwise.</param>
/// <param name="Name">The name for a save or rename; ignored otherwise.</param>
/// <param name="Values">The values for a save or update; ignored otherwise.</param>
public sealed record DevicePowerUserPresetEdit(
    DevicePowerUserPresetEditKind Kind,
    string PluginId,
    string Id,
    string? Name = null,
    DevicePowerCustomValues? Values = null);
