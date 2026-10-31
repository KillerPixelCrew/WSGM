using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Profile context captured before resolving or saving a device power assignment.</summary>
/// <param name="Profiles">Saved layers and application selection used for the edit.</param>
/// <param name="PluginId">Current package identifier, or null when no package is selected.</param>
/// <param name="Enabled">Whether device integration is enabled.</param>
/// <param name="OnAc">True on AC, false on battery, or null when the source is unknown.</param>
internal sealed record DevicePowerAssignmentContext(
    ProfileSnapshot Profiles,
    string? PluginId,
    bool Enabled,
    bool? OnAc)
{
    /// <summary>Active profile application identifier, or null for desktop/global context.</summary>
    internal string? ApplicationId => Profiles.Active.ApplicationId;

    /// <summary>The assignment in force for a power source, and the layer it came from.</summary>
    /// <param name="ac">True for the AC assignment; false for battery.</param>
    /// <returns>The effective assignment, possibly null, and its originating profile layer.</returns>
    internal Resolved<DevicePowerPresetReference?> Resolve(bool ac)
    {
        return ac
            ? Profiles.Layers.Reference(values => values.AcPowerPreset)
            : Profiles.Layers.Reference(values => values.BatteryPowerPreset);
    }
}
