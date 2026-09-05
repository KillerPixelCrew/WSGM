using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

internal sealed class NativeQamPowerPresetService(DevicePowerPresets? presets, DevicePowerAssignments? assignments) : ISteamPowerPresetBackend
{
    internal async ValueTask<SteamPowerPresetState?> ReadAsync()
    {
        if (presets is null) { return new(false, [], string.Empty, string.Empty, "", "", "", "Manual selection"); }
        DevicePowerPresetState state = await presets.ReadAsync().ConfigureAwait(false);
        SteamPowerProfileOption[] options = state.Presets.Select(item => new SteamPowerProfileOption(item.Id, item.Name)).ToArray();
        DevicePowerAssignmentState? selection = assignments?.Snapshot();
        string current = state.Presets.FirstOrDefault(item => item.Id == state.Current)?.Name
            ?? (state.Current == "custom" ? "Custom" : "Unavailable");
        return new(state.Available && assignments is not null, options, current,
            string.IsNullOrEmpty(selection?.Status) ? state.Status : selection.Status,
            selection?.AcPreset ?? "", selection?.BatteryPreset ?? "", selection?.Scope ?? "",
            selection?.Scope.StartsWith("Per-game", System.StringComparison.Ordinal) == true ? "Use global assignment" : "Manual selection");
    }

    public async Task<SteamUiCommandResult> SetAssignmentAsync(bool ac, string? option, CancellationToken cancellationToken)
    {
        if (assignments is null) { return new(false, "Device power assignments are unavailable."); }
        try
        {
            await assignments.AssignAsync(ac, option, cancellationToken).ConfigureAwait(false);
            string status = assignments.Snapshot().Status;
            return new(string.IsNullOrEmpty(status), string.IsNullOrEmpty(status) ? null : status);
        }
        catch (System.OperationCanceledException) { throw; }
        catch (System.InvalidOperationException ex) { return new(false, ex.Message); }
    }
}
