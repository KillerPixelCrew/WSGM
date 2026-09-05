using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed record DevicePowerAssignmentContext(
    PerformanceConfig Config, string? ApplicationId, string? PluginId, long Cycle, bool Enabled, bool? OnAc);

internal sealed record DevicePowerAssignmentState(string Scope, string? AcPreset, string? BatteryPreset, string Status);

/// <summary>Applies a saved assignment once per source, application, configuration or device-cycle change.</summary>
internal sealed class DevicePowerAssignments(
    DevicePowerPresets presets,
    Func<DevicePowerAssignmentContext> context,
    Func<DevicePowerAssignmentContext, bool, DevicePowerPresetReference?, Task> save)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (long Cycle, string? Application, bool Ac, string? Plugin, string? Preset)? _attempted;
    private string _status = string.Empty;

    internal DevicePowerAssignmentState Snapshot()
    {
        var current = context();
        var application = Application(current);
        var ac = application is null ? current.Config.AcPowerPreset : application.AcPowerPreset;
        var battery = application is null ? current.Config.BatteryPowerPreset : application.BatteryPowerPreset;
        return new(application is null ? "Global assignments" : "Per-game assignments (unset values use global)",
            ac is not null && ac.PluginId == current.PluginId ? ac.PresetId : null,
            battery is not null && battery.PluginId == current.PluginId ? battery.PresetId : null, _status);
    }

    internal bool HasCurrentAssignment
    {
        get
        {
            var current = context();
            var application = Application(current);
            var assignment = current.OnAc == true ? application?.AcPowerPreset ?? current.Config.AcPowerPreset
                : current.OnAc == false ? application?.BatteryPowerPreset ?? current.Config.BatteryPowerPreset : null;
            return current.Enabled && assignment?.PluginId == current.PluginId && assignment is not null;
        }
    }

    internal async Task AssignAsync(bool ac, string? id, CancellationToken cancellationToken)
    {
        var current = context();
        var state = await presets.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (id is not null && (current.PluginId is null || !state.Presets.Any(preset => preset.Id == id)))
        {
            throw new InvalidOperationException("This device power profile is no longer available.");
        }
        await save(current, ac, id is null ? null : new DevicePowerPresetReference
        { PluginId = current.PluginId!, PresetId = id }).ConfigureAwait(false);
        // Saving is an explicit user action, including selecting the same assignment after a failure.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _attempted = null; }
        finally { _gate.Release(); }
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = context();
            if (!current.Enabled || current.OnAc is not { } ac) { return; }
            var application = Application(current);
            var assignment = ac ? application?.AcPowerPreset ?? current.Config.AcPowerPreset
                : application?.BatteryPowerPreset ?? current.Config.BatteryPowerPreset;
            var key = (current.Cycle, current.ApplicationId, ac, assignment?.PluginId, assignment?.PresetId);
            if (_attempted == key) { return; }
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
            if (!state.Available) { return; }
            var confirmed = context();
            if (!confirmed.Enabled || confirmed.Cycle != current.Cycle || confirmed.OnAc != current.OnAc
                || confirmed.ApplicationId != current.ApplicationId || confirmed.PluginId != current.PluginId
                || !ReferenceEquals(confirmed.Config, current.Config)) { return; }
            // Record before dispatch. Uncertainty or a timeout must never cause a polling retry.
            _attempted = key;
            var result = await presets.ApplyAsync(assignment.PresetId, cancellationToken, persistValues: false).ConfigureAwait(false);
            _status = result.Succeeded ? string.Empty : result.Error ?? "The assigned profile could not be applied.";
        }
        finally { _gate.Release(); }
    }

    internal static PerformanceApplicationConfig? Application(DevicePowerAssignmentContext current) =>
        current.Config.Applications.FirstOrDefault(application => application.UsePerGameProfile
            && application.ApplicationId == current.ApplicationId);
}
