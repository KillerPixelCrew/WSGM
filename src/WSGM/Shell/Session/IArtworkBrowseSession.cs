using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>Owns one artwork browsing lifetime; dispose when the surface closes.</summary>
internal interface IArtworkBrowseSession : ISteamArtworkBrowserBackend, IChangeSource, IDisposable
{
    /// <summary>Reads the current artwork projection without starting a query.</summary>
    /// <returns>The opened game’s state, or null before a game has been opened.</returns>
    SteamArtworkBrowserState? ReadState();

    /// <summary>Opens one game and starts gathering its artwork candidates.</summary>
    /// <param name="id">Nonzero Steam AppID.</param>
    /// <param name="title">Title hint for shortcuts Steam has not listed yet, or null.</param>
    /// <param name="token">Cancels request admission; accepted browsing runs under the session lifetime.</param>
    /// <returns>Whether browsing was accepted; completion does not wait for candidate loading.</returns>
    Task<SteamUiCommandResult> OpenAsync(uint id, string? title, CancellationToken token);

    /// <summary>Queries Steam’s game list within the browsing lifetime.</summary>
    /// <returns>Available games or a result describing why the list could not be read.</returns>
    Task<SteamLibraryReadResult> ReadGamesAsync();

    /// <summary>Reads the opened game’s saved logo placement.</summary>
    /// <returns>The saved placement, or null when absent or unreadable; callers use default placement.</returns>
    Task<SteamLogoPosition?> ReadLogoPositionAsync();

    /// <summary>Cancels the current lookup generation and publishes a nonloading state; in-flight work is not joined.</summary>
    void CancelBrowsing();
}
