using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

internal interface IThemeBrowseSession : ISteamThemesBackend, IChangeSource, IDisposable
{
    string Tab { get; set; }
    SteamThemesState ReadState();
    void CancelQueries();
}

internal interface IAnimationBrowseSession : ISteamAnimationsBackend, IChangeSource, IDisposable
{
    string Tab { get; set; }
    SteamAnimationsState ReadState();
    string? PreviewPath(string id);
}

internal interface IArtworkBrowseSession : ISteamArtworkBrowserBackend, IChangeSource, IDisposable
{
    SteamArtworkBrowserState? ReadState();
    Task<SteamUiCommandResult> OpenAsync(uint id, string? title, CancellationToken token);
    Task<OverlayLibraryResult> ReadGamesAsync();
    Task<SteamLogoPosition?> ReadLogoPositionAsync();
    void CancelBrowsing();
}

internal interface IGameLibraryOverlaySource : IGameLibraryBackend, IChangeSource
{
    GameLibraryState ReadState();
    GameLibraryDetails? ReadDetails(string id);
    GameLibraryOptionsAnswer? ReadArtworkOptions(string id, string asset);
}
