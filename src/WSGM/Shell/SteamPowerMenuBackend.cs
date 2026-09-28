using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>Steam's Switch to Desktop entry in the Big Picture power menu, answered by WSGM's desktop switch.</summary>
/// <remarks>
///     The entry is published only in Game Mode: on the desktop Big Picture is an ordinary window and
///     there is no desktop to return to. The switch itself is the session's, the same one the
///     overlay's Return to Desktop runs.
/// </remarks>
internal sealed class SteamPowerMenuBackend(Func<bool> inGameMode, Func<CancellationToken, Task<bool>> switchToDesktop)
    : ISteamPowerMenuBackend
{
    /// <summary>Whether the entry is drawn right now.</summary>
    /// <returns>The state to publish.</returns>
    internal SteamPowerMenuState ReadState()
    {
        return new SteamPowerMenuState(inGameMode());
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SwitchToDesktopAsync(CancellationToken cancellationToken)
    {
        if (!inGameMode())
        {
            return new SteamUiCommandResult(false, "WSGM is not in Game Mode.");
        }

        return await switchToDesktop(cancellationToken).ConfigureAwait(false)
            ? SteamUiCommandResult.Applied
            : new SteamUiCommandResult(false, "A mode switch is already in progress.");
    }
}
