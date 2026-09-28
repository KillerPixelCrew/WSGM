using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>Steam's Switch to Desktop entry in the Big Picture power menu, answered by WSGM's desktop switch.</summary>
/// <remarks>
///     The entry is published only in Game Mode: on the desktop Big Picture is an ordinary window and
///     there is no desktop to return to. The switch itself is the session's, the same one the
///     overlay's Return to Desktop runs, and it decides on the UI thread whether it may start.
/// </remarks>
/// <param name="inGameMode">Whether the session starts in Game Mode.</param>
/// <param name="switchToDesktop">Starts the session's desktop switch, or says why it cannot.</param>
internal sealed class SteamPowerMenuBackend(
    bool inGameMode,
    Func<CancellationToken, Task<SteamUiCommandResult>> switchToDesktop) : ISteamPowerMenuBackend
{
    private volatile bool _inGameMode = inGameMode;

    /// <summary>Raised when the entry appears or disappears.</summary>
    internal event Action? Changed;

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SwitchToDesktopAsync(CancellationToken cancellationToken)
    {
        return switchToDesktop(cancellationToken);
    }

    /// <summary>Records the session's mode, which decides whether the entry is drawn.</summary>
    /// <param name="inGameMode">Whether the session is in Game Mode.</param>
    internal void SetGameMode(bool inGameMode)
    {
        if (_inGameMode == inGameMode)
        {
            return;
        }

        _inGameMode = inGameMode;
        Changed?.Invoke();
    }

    /// <summary>Whether the entry is drawn right now.</summary>
    /// <returns>The state to publish.</returns>
    internal SteamPowerMenuState ReadState()
    {
        return new SteamPowerMenuState(_inGameMode);
    }
}
