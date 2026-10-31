using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Hears what the library badge reports back: Steam's Big Picture Home layout.</summary>
/// <remarks>
///     The badge sits on the tile and moves with the carousel in either layout, so nothing here
///     changes placement. The report is logged on every transition so a pasted <c>wsgm.log</c> says
///     which layout Steam was in, which is the fact the September 2026 beta made worth knowing.
///     <see cref="Log.Change" /> already writes only a changed line.
/// </remarks>
internal sealed class LibraryBadgeBackend(
    Func<uint, CancellationToken, Task<SteamUiCommandResult>>? recheck = null) : ISteamLibraryBadgeBackend
{
    /// <inheritdoc />
    public Task<SteamUiCommandResult> RecheckAsync(uint appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return recheck?.Invoke(appId, cancellationToken) ?? Task.FromResult(SteamUiCommandResult.Refused);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> HomeLayoutAsync(bool bigArt, CancellationToken cancellationToken)
    {
        Log.Change("steam.home.layout", $"Steam Home layout: Big Art Mode {(bigArt ? "on" : "off")}.");
        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}
