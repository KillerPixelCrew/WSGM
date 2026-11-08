using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Applies a saved assignment once per source, application, configuration or device-cycle change.</summary>
/// <param name="presets">Shared one-shot device/Windows preset owner.</param>
/// <param name="context">Reads current profile, package, generation, and power-source context.</param>
/// <param name="save">Persists an assignment in the captured edit layer; null removes that layer's assignment.</param>
/// <param name="restoreFirst">
///     Optional task indicating initial desired-value restoration; incomplete restoration defers
///     reconciliation.
/// </param>
/// <param name="editPresets">
///     Persists one change to the device family's saved presets in the captured context; null when saved presets
///     cannot be edited.
/// </param>
internal sealed class DevicePowerAssignments(
    DevicePowerPresets presets,
    Func<DevicePowerAssignmentContext> context,
    Func<DevicePowerAssignmentContext, bool, DevicePowerPresetReference?, Task> save,
    Func<Task>? restoreFirst = null,
    Func<DevicePowerAssignmentContext, DevicePowerUserPresetEdit, Task>? editPresets = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _applied;
    private (string? Application, bool Ac, DevicePowerPresetReference? Assignment)? _attempted;
    private string _status = string.Empty;

    /// <summary>Whether integration is enabled and the known power source resolves an assignment for this package.</summary>
    internal bool HasCurrentAssignment
    {
        get
        {
            var current = context();
            var assignment = current.OnAc is { } ac ? current.Resolve(ac).Value : null;
            return current.Enabled && assignment?.PluginId == current.PluginId && assignment is not null;
        }
    }

    /// <summary>
    ///     Raised on the working thread after an assignment was saved or a reconcile pass ran, so after
    ///     every input the assignments follow: the power source, the Windows power mode, the profile,
    ///     the device cycle and the power controls' own readings.
    /// </summary>
    internal event Action? Changed;

    /// <summary>Projects the editor's own layer and the effective source of each assignment.</summary>
    /// <returns>A captured editor state; AutoTDP projects the active source as custom without saving that display value.</returns>
    internal DevicePowerAssignmentState Snapshot()
    {
        var current = context();
        var game = current.Profiles.EditsGame ? current.Profiles.Game?.Values : null;
        var layer = game ?? current.Profiles.Config.Global;
        var ac = layer.AcPowerPreset;
        var battery = layer.BatteryPowerPreset;
        // While AutoTDP owns the power limits, the source in use reads Custom: no preset is in force.
        // It is shown, never saved, so switching AutoTDP off shows the saved assignment again.
        var automatic = presets.AutomaticPowerOwned ? current.OnAc : null;
        return new DevicePowerAssignmentState(
            game is null ? "Global assignments" : "Per-game assignments (unset values use global)",
            automatic == true ? "custom" : ac is not null && ac.PluginId == current.PluginId ? ac.PresetId : null,
            automatic == false ? "custom"
            : battery is not null && battery.PluginId == current.PluginId ? battery.PresetId : null, _status,
            game is null, current.Resolve(true).Source, current.Resolve(false).Source);
    }

    /// <summary>Validates an explicit assignment against current context, saves it, and reconciles once.</summary>
    /// <param name="ac">True to edit the AC assignment; false to edit battery.</param>
    /// <param name="id">Current declared preset identifier, or null to remove the layer's assignment.</param>
    /// <param name="cancellationToken">Cancels admission and application; an already saved assignment remains saved.</param>
    /// <returns>Completion of save and the applicable one-shot reconciliation attempt.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The profile/device/source context changed or the preset is no longer
    ///     declared.
    /// </exception>
    internal async Task AssignAsync(bool ac, string? id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = context();
            var state = await presets.ReadAsync(cancellationToken).ConfigureAwait(false);
            var confirmed = context();
            if (current.Profiles.Generation != confirmed.Profiles.Generation
                || current.ApplicationId != confirmed.ApplicationId
                || current.PluginId != confirmed.PluginId
                || current.Enabled != confirmed.Enabled ||
                current.OnAc != confirmed.OnAc)
            {
                throw new InvalidOperationException(
                    "The application, device, power source or configuration changed before saving the assignment.");
            }

            if (id is not null && (current.PluginId is null || state.Presets.All(preset => preset.Id != id)))
            {
                throw new InvalidOperationException("This device power profile is no longer available.");
            }

            await save(current, ac, id is null
                ? null
                : new DevicePowerPresetReference
                    { PluginId = current.PluginId!, PresetId = id }).ConfigureAwait(false);
            // Saving is an explicit user action, including selecting the same assignment after a failure.
            if (current.OnAc == ac)
            {
                _attempted = null;
                _applied = false;
            }

            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Saves the current Custom reading as a new preset of this device family. Nothing is written to hardware.</summary>
    /// <param name="name">The name the user chose; trimmed, and unique per device family.</param>
    /// <param name="cancellationToken">Cancels admission and the read.</param>
    /// <returns>The new preset's identifier.</returns>
    /// <exception cref="InvalidOperationException">There is nothing to save, or the name or context is refused.</exception>
    internal Task<string> SaveUserPresetAsync(string name, CancellationToken cancellationToken)
    {
        return EditUserPresetAsync(async (current, state) =>
        {
            var values = SavableValues(state);
            var id = DevicePowerUserPresets.NewId();
            await EditPresetsAsync(current, new DevicePowerUserPresetEdit(
                DevicePowerUserPresetEditKind.Save, current.PluginId!, id, name, values)).ConfigureAwait(false);
            Log.Info($"Power preset {id} saved from the current values.");
            return id;
        }, cancellationToken);
    }

    /// <summary>Renames a saved preset; its identifier and assignments stay.</summary>
    /// <param name="id">The saved preset.</param>
    /// <param name="name">The new name; trimmed, and unique per device family.</param>
    /// <param name="cancellationToken">Cancels admission.</param>
    /// <returns>Completion once the name is saved.</returns>
    /// <exception cref="InvalidOperationException">The preset is gone, or the name or context is refused.</exception>
    internal Task RenameUserPresetAsync(string id, string name, CancellationToken cancellationToken)
    {
        return EditUserPresetAsync(async (current, _) =>
        {
            await EditPresetsAsync(current, new DevicePowerUserPresetEdit(
                DevicePowerUserPresetEditKind.Rename, current.PluginId!, id, name)).ConfigureAwait(false);
            return id;
        }, cancellationToken);
    }

    /// <summary>
    ///     Replaces a saved preset's values with the current Custom reading, keeping its identifier and name.
    ///     Nothing is written to hardware: an assignment naming the preset keeps naming it, and the new values
    ///     apply the next time that assignment is applied.
    /// </summary>
    /// <param name="id">The saved preset.</param>
    /// <param name="cancellationToken">Cancels admission and the read.</param>
    /// <returns>Completion once the values are saved.</returns>
    /// <exception cref="InvalidOperationException">There is nothing to save, the preset is gone or the context changed.</exception>
    internal Task UpdateUserPresetAsync(string id, CancellationToken cancellationToken)
    {
        return EditUserPresetAsync(async (current, state) =>
        {
            var values = SavableValues(state);
            await EditPresetsAsync(current, new DevicePowerUserPresetEdit(
                DevicePowerUserPresetEditKind.Update, current.PluginId!, id, Values: values)).ConfigureAwait(false);
            Log.Info($"Power preset {id} updated with the current values.");
            return id;
        }, cancellationToken);
    }

    /// <summary>
    ///     Deletes a saved preset. Every assignment naming it becomes Custom holding its values, so nothing changes
    ///     on the device.
    /// </summary>
    /// <param name="id">The saved preset.</param>
    /// <param name="cancellationToken">Cancels admission.</param>
    /// <returns>Completion once the preset and its assignments are saved.</returns>
    /// <exception cref="InvalidOperationException">The preset is gone or the context changed.</exception>
    internal Task DeleteUserPresetAsync(string id, CancellationToken cancellationToken)
    {
        return EditUserPresetAsync(async (current, _) =>
        {
            await EditPresetsAsync(current, new DevicePowerUserPresetEdit(
                DevicePowerUserPresetEditKind.Delete, current.PluginId!, id)).ConfigureAwait(false);
            Log.Info($"Power preset {id} deleted; its assignments keep its values as Custom.");
            return id;
        }, cancellationToken);
    }

    private async Task<string> EditUserPresetAsync(
        Func<DevicePowerAssignmentContext, DevicePowerPresetState, Task<string>> edit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = context();
            if (!current.Enabled || current.PluginId is null)
            {
                throw new InvalidOperationException("Power presets need Device Integration and a supported device.");
            }

            var state = await presets.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Presets.Count == 0)
            {
                throw new InvalidOperationException("This device offers no power presets.");
            }

            var confirmed = context();
            if (confirmed.PluginId != current.PluginId || confirmed.Enabled != current.Enabled)
            {
                throw new InvalidOperationException("The device changed before the preset was saved.");
            }

            return await edit(current, state).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private DevicePowerCustomValues SavableValues(DevicePowerPresetState state)
    {
        if (presets.AutomaticPowerOwned)
        {
            throw new InvalidOperationException(
                "AutoTDP is moving the power limits. Pause it with a manual change before saving a preset.");
        }

        return state is { Savable: true, Values: { } values }
            ? values
            : throw new InvalidOperationException(
                "The current power values already match a preset or are not complete, so there is nothing to save.");
    }

    private Task EditPresetsAsync(DevicePowerAssignmentContext current, DevicePowerUserPresetEdit edit)
    {
        return editPresets?.Invoke(current, edit)
               ?? throw new InvalidOperationException("Saved power presets are unavailable.");
    }

    /// <summary>Assigns the next device preset for the current power source, in the layer in force.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>Whether a preset was assigned.</returns>
    /// <remarks>What the OEM "next performance profile" action does.</remarks>
    internal async Task<bool> CycleAsync(CancellationToken cancellationToken)
    {
        var current = context();
        if (!current.Enabled || current.OnAc is not { } ac)
        {
            return false;
        }

        var state = await presets.ReadAsync(cancellationToken).ConfigureAwait(false);
        var ids = state.Presets.Select(preset => preset.Id).ToArray();
        if (!state.Available || ids.Length == 0)
        {
            return false;
        }

        var index = Array.IndexOf(ids, current.Resolve(ac).Value?.PresetId ?? state.Current);
        await AssignAsync(ac, ids[(index + 1) % ids.Length], cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Applies a changed assignment once or records successfully applied values drifting to custom.</summary>
    /// <param name="cancellationToken">Cancels lane admission and remaining preset work.</param>
    /// <returns>Completion of this pass; unknown/unavailable inputs or pending initial restoration defer work.</returns>
    /// <remarks>
    ///     An uncertain/failed attempt is not retried for the same context. Explicit assignment edits permit another
    ///     attempt.
    /// </remarks>
    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Makes the next reconcile apply the assignment again; a new device cycle starts on firmware values.</summary>
    /// <returns>Completion once any reconcile in progress has finished.</returns>
    internal async Task ForgetAppliedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _attempted = null;
            _applied = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        var current = context();
        if (!current.Enabled || current.OnAc is not { } ac)
        {
            return;
        }

        // Desired-value restoration precedes presets on the shared device command lane.
        if (restoreFirst?.Invoke() is { IsCompleted: false })
        {
            return;
        }

        var assignment = current.Resolve(ac).Value;
        var key = (current.ApplicationId, ac, assignment);
        var alreadyAttempted = _attempted == key;
        if (alreadyAttempted && !_applied)
        {
            return;
        }

        if (assignment is null)
        {
            _attempted = key;
            _status = string.Empty;
            return;
        }

        if (assignment.PluginId != current.PluginId)
        {
            _attempted = key;
            _status = "The assigned profile belongs to another device plugin.";
            return;
        }

        // AutoTDP's limits move on their own; saving them would turn its output into the user's Custom
        // profile. Checked before the read, because every AutoTDP step wakes this reconcile.
        if (alreadyAttempted && presets.AutomaticPowerOwned)
        {
            return;
        }

        var state = await presets.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!state.Available)
        {
            return;
        }

        var confirmed = context();
        if (!confirmed.Enabled || confirmed.OnAc != current.OnAc
                               || confirmed.ApplicationId != current.ApplicationId ||
                               confirmed.PluginId != current.PluginId
                               || confirmed.Profiles.Generation != current.Profiles.Generation)
        {
            return;
        }

        if (alreadyAttempted)
        {
            var changed = assignment.CustomValues is { } custom
                ? state.Values != custom
                : state.Current != assignment.PresetId;
            if (!changed || state.Values is not { SustainedWatts: > 0 } values
                         || values.SlowWatts < values.SustainedWatts)
            {
                return;
            }

            var customAssignment = new DevicePowerPresetReference
                { PluginId = current.PluginId!, PresetId = "custom", CustomValues = values };
            await save(current, ac, customAssignment).ConfigureAwait(false);
            _attempted = (current.ApplicationId, ac, customAssignment);
            _status = string.Empty;
            return;
        }

        // Reuse a successful matching assignment across application switches on the same device cycle/source.
        // A deleted saved preset turns into Custom holding its values, which the device already runs.
        if (_applied
            && _attempted is { } previous
            && previous.Ac == ac
            && (previous.Assignment == assignment
                || (assignment.CustomValues is not null
                    && previous.Assignment?.PluginId == assignment.PluginId
                    && DevicePowerUserPresets.IsUserId(previous.Assignment?.PresetId)))
            && (assignment.CustomValues is { } customValues
                ? state.Values == customValues
                : state.Current == assignment.PresetId))
        {
            _attempted = key;
            _status = string.Empty;
            return;
        }

        // Record before dispatch. Uncertainty or a timeout must never cause a polling retry.
        _attempted = key;
        _applied = false;
        var result = await presets.ApplyAsync(assignment.PresetId, cancellationToken, false, ac,
            assignment.CustomValues).ConfigureAwait(false);
        _applied = result.Succeeded;
        _status = result.Succeeded ? string.Empty : result.Error ?? "The assigned profile could not be applied.";
    }
}
