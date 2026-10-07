using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Projects device presets and profile-aware power-source assignments into Steam.</summary>
/// <param name="presets">Borrowed device preset reader, or null when the device offers no presets.</param>
/// <param name="assignments">Borrowed assignment owner, or null when automatic/profile assignment is unavailable.</param>
internal sealed class NativeQamPowerPresetService(DevicePowerPresets? presets, DevicePowerAssignments? assignments)
    : ISteamPowerPresetBackend
{
    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetAssignmentAsync(bool ac, string? option,
        CancellationToken cancellationToken)
    {
        if (assignments is null)
        {
            return new SteamUiCommandResult(false, "Device power assignments are unavailable.");
        }

        try
        {
            await assignments.AssignAsync(ac, option, cancellationToken).ConfigureAwait(false);
            var status = assignments.Snapshot().Status;
            return new SteamUiCommandResult(string.IsNullOrEmpty(status), string.IsNullOrEmpty(status) ? null : status);
        }
        catch (InvalidOperationException ex)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }
    }

    /// <summary>Reads device presets and combines them with the current AC/battery assignment scope.</summary>
    /// <returns>Choices, observed preset and assignments; unavailable dependencies produce an unavailable state, and Custom is a display-only option.</returns>
    internal async ValueTask<SteamPowerPresetState?> ReadAsync()
    {
        if (presets is null)
        {
            return new SteamPowerPresetState(false, [], string.Empty, string.Empty, "", "", "", "Manual selection");
        }

        var state = await presets.ReadAsync().ConfigureAwait(false);
        var options = state.Presets.Select(item => new SteamPowerProfileOption(item.Id, item.Name)).ToArray();
        var selection = assignments?.Snapshot();
        if (selection?.AcPreset == "custom" || selection?.BatteryPreset == "custom")
        {
            // Values that match none of the presets: listed where they are the current assignment,
            // never offered as a choice.
            options = [.. options, new SteamPowerProfileOption("custom", "Custom", false)];
        }

        var current = state.Presets.FirstOrDefault(item => item.Id == state.Current)?.Name
                      ?? (state.Current == "custom" ? "Custom" : "Unavailable");
        return new SteamPowerPresetState(state.Available && assignments is not null, options, current,
            string.IsNullOrEmpty(selection?.Status) ? state.Status : selection.Status,
            selection?.AcPreset ?? "", selection?.BatteryPreset ?? "", selection?.Scope ?? "",
            selection?.IsGlobal == false ? "Use global assignment" : "Manual selection",
            selection?.AcSource is ProfileSource.Game,
            selection?.BatterySource is ProfileSource.Game);
    }
}
