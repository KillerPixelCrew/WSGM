using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Windows' processor boost mode for Steam's Performance dropdown, as a per-game profile value.
/// </summary>
/// <remarks>
///     The same <see cref="CpuBoostService" /> path the overlay row uses, so a change
///     from either surface is the same Windows write and the same profile save, and the next publication
///     on the other reports it. Every publication reads Windows: a scheme switch can carry a different
///     mode with it.
/// </remarks>
/// <param name="cpuBoost">Borrowed owner of Windows boost reads, writes and profile saves.</param>
/// <param name="profiles">Borrowed profile owner used to annotate the effective selection and override source.</param>
internal sealed class NativeQamCpuBoostService(CpuBoostService cpuBoost, ProfileService profiles)
    : ISteamCpuBoostBackend
{
    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetCpuBoostAsync(string option, CancellationToken cancellationToken)
    {
        if (CpuBoost.ModeForId(option) is not { } mode)
        {
            return new SteamUiCommandResult(false, "Unknown processor boost mode.");
        }

        return await cpuBoost.SetFromUserAsync(mode, cancellationToken).ConfigureAwait(false)
            ? new SteamUiCommandResult(true, null)
            : new SteamUiCommandResult(false, "Windows did not confirm the processor boost mode.");
    }

    /// <summary>Refreshes Windows boost capabilities and projects the effective profile preference.</summary>
    /// <returns>Offered modes and effective selection, or an unavailable state with the read failure or unsupported reason.</returns>
    internal async ValueTask<SteamCpuBoostState?> ReadAsync()
    {
        CpuBoostStatus? status;
        try
        {
            status = await cpuBoost.RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SteamCpuBoostState(false, [], string.Empty, ex.Message);
        }

        if (status is not { Supported: true } || cpuBoost.View(profiles.Current.Layers) is not { } view)
        {
            // Published as unavailable rather than withheld, so an absent control can be told from a
            // broken one through the component host's render outcomes.
            return new SteamCpuBoostState(false, [], string.Empty,
                "The active power scheme does not expose a processor boost mode.");
        }

        var scope = view.Source switch
        {
            ProfileSource.Game => "Set for this game.",
            ProfileSource.Global => "From Global.",
            _ => "Not set by WSGM; Windows keeps its own value."
        };
        return new SteamCpuBoostState(
            true,
            [
                .. CpuBoost.Offered.Select(option =>
                    new SteamPowerProfileOption(CpuBoost.IdFor(option.Mode), option.Name))
            ],
            view.Effective is { } mode ? CpuBoost.IdFor(mode) : string.Empty,
            $"{scope} {view.SourcesText}",
            view.Source is ProfileSource.Game);
    }
}
