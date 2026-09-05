using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

internal sealed class NativeQamPowerPresetService(DevicePowerPresets? presets) : ISteamPowerProfileBackend
{
    internal async ValueTask<SteamPowerProfileState?> ReadAsync()
    {
        if (presets is null) { return new(false, [], string.Empty, string.Empty); }
        DevicePowerPresetState state = await presets.ReadAsync().ConfigureAwait(false);
        SteamPowerProfileOption[] options = state.Presets.Select(item => new SteamPowerProfileOption(item.Id, item.Name)).ToArray();
        if (state.Current == "custom") { options = [new("custom", "Custom"), .. options]; }
        return new(state.Available, options, state.Current, state.Status);
    }

    public Task<SteamUiCommandResult> SetPowerProfileAsync(string option, CancellationToken cancellationToken) =>
        presets?.ApplyAsync(option, cancellationToken)
        ?? Task.FromResult(new SteamUiCommandResult(false, "Device power profiles are unavailable."));
}
