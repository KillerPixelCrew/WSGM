using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The device preset assignments as one row projection: Steam's Device performance rows and the overlay's
///     power assignments both read and assign through it, so they show the same choices, labels and status.
/// </summary>
internal static class PowerPresetRows
{
    /// <summary>Reads the device presets and combines them with the current plugged-in and battery assignments.</summary>
    /// <param name="presets">The device preset reader, or null when the device offers none.</param>
    /// <param name="assignments">The assignment owner, or null when assignment is unavailable.</param>
    /// <returns>
    ///     Choices, the preset in effect and the assignments; unavailable dependencies produce an unavailable state, and
    ///     Custom is listed unselectable while an assignment holds it.
    /// </returns>
    internal static async ValueTask<PowerPresetRowState> ReadAsync(DevicePowerPresets? presets,
        DevicePowerAssignments? assignments)
    {
        if (presets is null)
        {
            return new PowerPresetRowState(false, [], string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, "Manual selection");
        }

        var state = await presets.ReadAsync().ConfigureAwait(false);
        ChoiceRowOption[] options = [.. state.Presets.Select(item => new ChoiceRowOption(item.Id, item.Name))];
        var selection = assignments?.Snapshot();
        if (selection?.AcPreset == "custom" || selection?.BatteryPreset == "custom")
        {
            // Values that match none of the presets: listed where they are the current assignment, never
            // offered as a choice.
            options = [.. options, new ChoiceRowOption("custom", "Custom", false)];
        }

        var current = state.Presets.FirstOrDefault(item => item.Id == state.Current)?.Name
                      ?? (state.Current == "custom" ? "Custom" : "Unavailable");
        return new PowerPresetRowState(state.Available && assignments is not null, options, current,
            string.IsNullOrEmpty(selection?.Status) ? state.Status : selection.Status,
            selection?.AcPreset ?? string.Empty, selection?.BatteryPreset ?? string.Empty,
            selection?.Scope ?? string.Empty,
            selection?.IsGlobal == false ? "Use global assignment" : "Manual selection",
            selection?.AcSource is ProfileSource.Game,
            selection?.BatterySource is ProfileSource.Game);
    }

    /// <summary>Assigns a preset to one power source.</summary>
    /// <param name="assignments">The assignment owner, or null when assignment is unavailable.</param>
    /// <param name="ac">True for plugged in, false for battery.</param>
    /// <param name="option">The preset id, or null for none.</param>
    /// <param name="cancellationToken">Cancels the assignment.</param>
    /// <returns>Applied, or the owner's status as the refusal.</returns>
    internal static async Task<SteamUiCommandResult> AssignAsync(DevicePowerAssignments? assignments, bool ac,
        string? option, CancellationToken cancellationToken)
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
}
