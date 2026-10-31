using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Hears what the Home carousel holds and writes it to the log.</summary>
/// <remarks>
///     The report is the carousel's own account, once per change, so a pasted <c>wsgm.log</c> says what
///     Home showed after a card was pulled or a game installed without anyone attaching to Steam.
/// </remarks>
internal sealed class HomeCarouselBackend : ISteamHomeCarouselBackend
{
    /// <inheritdoc />
    public Task<SteamUiCommandResult> ReportAsync(
        SteamHomeCarouselReport report,
        CancellationToken cancellationToken)
    {
        Log.Change(
            "steam.home.carousel",
            report.Fallback
                ? $"Home carousel: nothing on the attached libraries qualified; showing Steam's own {report.Items} entries."
                : $"Home carousel: {report.Items} entries ({report.Purchases} new purchases, {report.Installed} installed, "
                  + $"{report.Uninstalled} uninstalled), {report.Excluded} games on disconnected libraries left out, "
                  + $"collection tracking {(report.Tracking ? "on" : "off")}.");
        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}
