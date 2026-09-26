using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Windows' processor boost mode for Steam's Performance dropdown, as a per-game profile value.
/// </summary>
/// <remarks>
///     The same <see cref="ApplicationPerformanceReconciler" /> path the overlay row uses, so a change
///     from either surface is the same Windows write and the same profile save, and the next publication
///     on the other reports it. Every publication reads Windows: a scheme switch can carry a different
///     mode with it.
/// </remarks>
internal sealed class NativeQamCpuBoostService(ApplicationPerformanceReconciler reconciler, ProfileService profiles)
    : ISteamCpuBoostBackend
{
    public async Task<SteamUiCommandResult> SetCpuBoostAsync(string option, CancellationToken cancellationToken)
    {
        if (CpuBoost.ModeForId(option) is not { } mode)
        {
            return new SteamUiCommandResult(false, "Unknown processor boost mode.");
        }

        return await reconciler.SetCpuBoostFromUserAsync(mode, cancellationToken).ConfigureAwait(false)
            ? new SteamUiCommandResult(true, null)
            : new SteamUiCommandResult(false, "Windows did not confirm the processor boost mode.");
    }

    internal async ValueTask<SteamCpuBoostState?> ReadAsync()
    {
        CpuBoostStatus? status;
        try
        {
            status = await reconciler.RefreshCpuBoostAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SteamCpuBoostState(false, [], string.Empty, ex.Message, null);
        }

        if (status is not { Supported: true })
        {
            // Published as unavailable rather than withheld, so an absent control can be told from a
            // broken one through the component host's render outcomes.
            return new SteamCpuBoostState(false, [], string.Empty,
                "The active power scheme does not expose a processor boost mode.", null);
        }

        var preference = profiles.Current.Layers.Value(values => values.CpuBoost);
        var effective = preference.Value ?? status.OnAc;
        var scope = preference.Source switch
        {
            ProfileSource.Game => "Set for this game.",
            ProfileSource.Global => "From Global.",
            _ => "Not set by WSGM; Windows keeps its own value."
        };
        var sources = status.OnAc == status.OnBattery
            ? "Applies to both plugged in and battery."
            : "Plugged in and battery currently differ; choosing sets both.";
        return new SteamCpuBoostState(
            true,
            [.. CpuBoost.Offered.Select(option => new SteamPowerProfileOption(CpuBoost.IdFor(option.Mode), option.Name))],
            effective is { } mode ? CpuBoost.IdFor(mode) : string.Empty,
            $"{scope} {sources}",
            preference.Source is ProfileSource.Game ? nameof(ProfileField.CpuBoost) : null);
    }
}
