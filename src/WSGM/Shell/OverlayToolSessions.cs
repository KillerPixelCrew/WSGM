using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>Owns one surface’s theme browse state; disposing cancels its queries without disposing the shared theme service.</summary>
internal interface IThemeBrowseSession : ISteamThemesBackend, IChangeSource, IDisposable
{
    /// <summary>Local tab selection; changing it directly does not publish a state notification.</summary>
    string Tab { get; set; }
    /// <summary>Reads the current theme projection without starting a query.</summary>
    /// <returns>Shared installed state combined with this session’s browse state.</returns>
    SteamThemesState ReadState();
    /// <summary>Requests cancellation of browse and detail queries without joining them or publishing a notification.</summary>
    void CancelQueries();
    /// <summary>Persists whether theme styles are active.</summary>
    /// <param name="enabled">Whether the shared theme service should publish styles.</param>
    /// <param name="cancellationToken">Cancels waiting and cooperative persistence work.</param>
    /// <returns>The persistence outcome; all surfaces observe the shared setting.</returns>
    Task<SteamUiCommandResult> SetThemesEnabledAsync(bool enabled, CancellationToken cancellationToken);
    /// <summary>Changes the shared class-translation branch used by themes.</summary>
    /// <param name="branch">Supported translation branch name.</param>
    /// <param name="cancellationToken">Cancels waiting and cooperative work.</param>
    /// <returns>The command acceptance or refusal; subsequent translation loading may publish further changes.</returns>
    Task<SteamUiCommandResult> SetTranslationsBranchAsync(string branch, CancellationToken cancellationToken);
}

/// <summary>Owns one surface’s animation selection and detail state over a borrowed shared library.</summary>
internal interface IAnimationBrowseSession : ISteamAnimationsBackend, IChangeSource, IDisposable
{
    /// <summary>Local tab selection; changing it directly does not publish a state notification.</summary>
    string Tab { get; set; }
    /// <summary>Reads the current animation projection without starting a query.</summary>
    /// <returns>Shared library state combined with this session’s tab and detail selection.</returns>
    SteamAnimationsState ReadState();
    /// <summary>Finds a local preview for an installed movie.</summary>
    /// <param name="id">Animation library identifier.</param>
    /// <returns>Local preview path, or null when no installed preview is available.</returns>
    string? PreviewPath(string id);
}

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
    Task<OverlayLibraryResult> ReadGamesAsync();
    /// <summary>Reads the opened game’s saved logo placement.</summary>
    /// <returns>The saved placement, or null when absent or unreadable; callers use default placement.</returns>
    Task<SteamLogoPosition?> ReadLogoPositionAsync();
    /// <summary>Cancels the current lookup generation and publishes a nonloading state; in-flight work is not joined.</summary>
    void CancelBrowsing();
}

/// <summary>Projects the shared library backend for overlay consumers; the session owns its lifetime.</summary>
internal interface IGameLibraryOverlaySource : IGameLibraryBackend, IChangeSource
{
    /// <summary>Reads the shared library’s latest published review.</summary>
    /// <returns>A snapshot for rendering; no scan or Steam query is started.</returns>
    GameLibraryState ReadState();
    /// <summary>Reads evidence already gathered for one review entry.</summary>
    /// <param name="id">Stable review entry identifier.</param>
    /// <returns>Entry details, or null when the entry is no longer listed.</returns>
    GameLibraryDetails? ReadDetails(string id);
    /// <summary>Reads cached candidates for one review entry’s artwork slot.</summary>
    /// <param name="id">Stable review entry identifier.</param>
    /// <param name="asset">Artwork slot: grid, wide, hero, logo or icon.</param>
    /// <returns>Candidates and loading status, or null when the entry or slot is unavailable.</returns>
    GameLibraryOptionsAnswer? ReadArtworkOptions(string id, string asset);
}
