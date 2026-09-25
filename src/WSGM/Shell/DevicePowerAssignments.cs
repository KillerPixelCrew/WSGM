using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed record DevicePowerAssignmentContext(
    ProfileSnapshot Profiles,
    string? PluginId,
    long Cycle,
    bool Enabled,
    bool? OnAc)
{
    internal string? ApplicationId => Profiles.Active.ApplicationId;

    /// <summary>The assignment in force for a power source, and the layer it came from.</summary>
    internal Resolved<DevicePowerPresetReference?> Resolve(bool ac)
    {
        return ac
            ? Profiles.Layers.Reference(values => values.AcPowerPreset)
            : Profiles.Layers.Reference(values => values.BatteryPowerPreset);
    }
}

/// <summary>What the power-preset editor shows.</summary>
/// <param name="Scope">Which layer an edit lands in, for the caption.</param>
/// <param name="AcPreset">The edit layer's own AC assignment; null inherits in a game profile.</param>
/// <param name="BatteryPreset">The edit layer's own battery assignment.</param>
/// <param name="Status">Why the last apply failed, or empty.</param>
/// <param name="IsGlobal">Whether edits land in Global.</param>
/// <param name="AcSource">The layer supplying the AC assignment in force.</param>
/// <param name="BatterySource">The layer supplying the battery assignment in force.</param>
internal sealed record DevicePowerAssignmentState(
    string Scope,
    string? AcPreset,
    string? BatteryPreset,
    string Status,
    bool IsGlobal,
    ProfileSource AcSource = ProfileSource.None,
    ProfileSource BatterySource = ProfileSource.None);

/// <summary>Applies a saved assignment once per source, application, configuration or device-cycle change.</summary>
internal sealed class DevicePowerAssignments(
    DevicePowerPresets presets,
    Func<DevicePowerAssignmentContext> context,
    Func<DevicePowerAssignmentContext, bool, DevicePowerPresetReference?, Task> save,
    Func<Task>? restoreFirst = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _applied;
    private (long Cycle, string? Application, bool Ac, DevicePowerPresetReference? Assignment)? _attempted;
    private string _status = string.Empty;

    internal bool HasCurrentAssignment
    {
        get
        {
            var current = context();
            var assignment = current.OnAc is { } ac ? current.Resolve(ac).Value : null;
            return current.Enabled && assignment?.PluginId == current.PluginId && assignment is not null;
        }
    }

    internal DevicePowerAssignmentState Snapshot()
    {
        var current = context();
        var game = current.Profiles.EditsGame ? current.Profiles.Game?.Values : null;
        var layer = game ?? current.Profiles.Config.Global;
        var ac = layer.AcPowerPreset;
        var battery = layer.BatteryPowerPreset;
        // While AutoTDP owns the power limits, the source in use reads Custom: no preset is in force.
        // It is shown, never saved, so switching AutoTDP off shows the saved assignment again.
        var automatic = presets.AutomaticPowerOwner?.Invoke() == true ? current.OnAc : null;
        return new DevicePowerAssignmentState(
            game is null ? "Global assignments" : "Per-game assignments (unset values use global)",
            automatic == true ? "custom" : ac is not null && ac.PluginId == current.PluginId ? ac.PresetId : null,
            automatic == false ? "custom"
            : battery is not null && battery.PluginId == current.PluginId ? battery.PresetId : null, _status,
            game is null, current.Resolve(true).Source, current.Resolve(false).Source);
    }

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
                || current.PluginId != confirmed.PluginId ||
                current.Cycle != confirmed.Cycle
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
        }
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
        }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        var current = context();
        if (!current.Enabled || current.OnAc is not { } ac)
        {
            return;
        }

        // After a resume the device's desired values are restored first. The preset write shares the
        // plugin's one command lane, and competing with it is what left lighting zones unrestored.
        if (restoreFirst?.Invoke() is { IsCompleted: false })
        {
            return;
        }

        var assignment = current.Resolve(ac).Value;
        var key = (current.Cycle, current.ApplicationId, ac, assignment);
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

        var state = await presets.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!state.Available)
        {
            return;
        }

        var confirmed = context();
        if (!confirmed.Enabled || confirmed.Cycle != current.Cycle || confirmed.OnAc != current.OnAc
            || confirmed.ApplicationId != current.ApplicationId || confirmed.PluginId != current.PluginId
            || confirmed.Profiles.Generation != current.Profiles.Generation)
        {
            return;
        }

        if (alreadyAttempted)
        {
            // AutoTDP's limits move on their own; saving them would turn its output into the user's
            // Custom profile.
            if (presets.AutomaticPowerOwner?.Invoke() == true)
            {
                return;
            }

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
            _attempted = (current.Cycle, current.ApplicationId, ac, customAssignment);
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
