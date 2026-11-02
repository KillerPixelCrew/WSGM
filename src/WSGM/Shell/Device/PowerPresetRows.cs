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
        var saved = state.Saved ?? [];
        return new PowerPresetRowState(state.Available && assignments is not null, options, current,
            string.IsNullOrEmpty(selection?.Status) ? state.Status : selection.Status,
            selection?.AcPreset ?? string.Empty, selection?.BatteryPreset ?? string.Empty,
            selection?.Scope ?? string.Empty,
            selection?.IsGlobal == false ? "Use global assignment" : "Manual selection",
            selection?.AcSource is ProfileSource.Game,
            selection?.BatterySource is ProfileSource.Game,
            state.Available && state.Savable && assignments is not null && !presets.AutomaticPowerOwned,
            DevicePowerUserPresets.DefaultName(saved.Select(item => item.Preset),
                saved.FirstOrDefault()?.Preset.PluginId),
            state.Values is { } values ? DevicePowerPresets.Describe(values) : string.Empty,
            [
                .. saved.Select(item => new PowerPresetSavedRow(item.Preset.Id, item.Preset.Name,
                    DevicePowerPresets.Describe(item.Preset.Values!)
                    + (item.Fits ? string.Empty : " · Does not fit this device's current power limits"),
                    item.Fits, item.Preset.Id == state.Current))
            ]);
    }

    /// <summary>Saves the current Custom reading as a new preset.</summary>
    /// <param name="assignments">The assignment owner, or null when assignment is unavailable.</param>
    /// <param name="name">The preset's name.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Applied, or why the save was refused.</returns>
    internal static Task<SteamUiCommandResult> SaveAsync(DevicePowerAssignments? assignments, string name,
        CancellationToken cancellationToken)
    {
        return RunAsync(assignments, owner => owner.SaveUserPresetAsync(name, cancellationToken));
    }

    /// <summary>Renames a saved preset.</summary>
    /// <param name="assignments">The assignment owner, or null when assignment is unavailable.</param>
    /// <param name="id">The saved preset.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the rename.</param>
    /// <returns>Applied, or why the rename was refused.</returns>
    internal static Task<SteamUiCommandResult> RenameAsync(DevicePowerAssignments? assignments, string id,
        string name, CancellationToken cancellationToken)
    {
        return RunAsync(assignments, owner => owner.RenameUserPresetAsync(id, name, cancellationToken));
    }

    /// <summary>Replaces a saved preset's values with the current Custom reading.</summary>
    /// <param name="assignments">The assignment owner, or null when assignment is unavailable.</param>
    /// <param name="id">The saved preset.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>Applied, or why the update was refused.</returns>
    internal static Task<SteamUiCommandResult> UpdateAsync(DevicePowerAssignments? assignments, string id,
        CancellationToken cancellationToken)
    {
        return RunAsync(assignments, owner => owner.UpdateUserPresetAsync(id, cancellationToken));
    }

    /// <summary>Deletes a saved preset; its assignments become Custom holding its values.</summary>
    /// <param name="assignments">The assignment owner, or null when assignment is unavailable.</param>
    /// <param name="id">The saved preset.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>Applied, or why the delete was refused.</returns>
    internal static Task<SteamUiCommandResult> DeleteAsync(DevicePowerAssignments? assignments, string id,
        CancellationToken cancellationToken)
    {
        return RunAsync(assignments, owner => owner.DeleteUserPresetAsync(id, cancellationToken));
    }

    private static async Task<SteamUiCommandResult> RunAsync(DevicePowerAssignments? assignments,
        Func<DevicePowerAssignments, Task> command)
    {
        if (assignments is null)
        {
            return new SteamUiCommandResult(false, "Device power presets are unavailable.");
        }

        try
        {
            await command(assignments).ConfigureAwait(false);
            return new SteamUiCommandResult(true, null);
        }
        catch (InvalidOperationException ex)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }
        catch (ConfigUnavailableException ex)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }
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
