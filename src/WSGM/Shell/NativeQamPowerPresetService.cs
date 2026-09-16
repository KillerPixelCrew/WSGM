using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

internal sealed class NativeQamPowerPresetService(DevicePowerPresets? presets, DevicePowerAssignments? assignments) : ISteamPowerPresetBackend
{
    internal async ValueTask<SteamPowerPresetState?> ReadAsync()
    {
        if (presets is null) { return new SteamPowerPresetState(false, [], string.Empty, string.Empty, "", "", "", "Manual selection"); }
        var state = await presets.ReadAsync().ConfigureAwait(false);
        var options = state.Presets.Select(item => new SteamPowerProfileOption(item.Id, item.Name)).ToArray();
        var selection = assignments?.Snapshot();
        if (selection?.AcPreset == "custom" || selection?.BatteryPreset == "custom")
        { options = [.. options, new SteamPowerProfileOption("custom", "Custom")]; }
        var current = state.Presets.FirstOrDefault(item => item.Id == state.Current)?.Name
                      ?? (state.Current == "custom" ? "Custom" : "Unavailable");
        return new SteamPowerPresetState(state.Available && assignments is not null, options, current,
            string.IsNullOrEmpty(selection?.Status) ? state.Status : selection.Status,
            selection?.AcPreset ?? "", selection?.BatteryPreset ?? "", selection?.Scope ?? "",
            selection?.IsGlobal == false ? "Use global assignment" : "Manual selection");
    }

    public async Task<SteamUiCommandResult> SetAssignmentAsync(bool ac, string? option, CancellationToken cancellationToken)
    {
        if (assignments is null) { return new SteamUiCommandResult(false, "Device power assignments are unavailable."); }
        try
        {
            await assignments.AssignAsync(ac, option, cancellationToken).ConfigureAwait(false);
            var status = assignments.Snapshot().Status;
            return new SteamUiCommandResult(string.IsNullOrEmpty(status), string.IsNullOrEmpty(status) ? null : status);
        }
        catch (InvalidOperationException ex) { return new SteamUiCommandResult(false, ex.Message); }
    }
}
