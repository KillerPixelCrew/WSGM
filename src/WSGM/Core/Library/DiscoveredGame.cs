using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One game a source found, before anything has been decided about importing it.</summary>
/// <param name="SourceId">Which source found it.</param>
/// <param name="Key">Its stable identity within that source. The AUMID, for Xbox.</param>
/// <param name="Name">What to call it in the library.</param>
/// <param name="InstallPath">Where it is installed, for diagnostics.</param>
/// <param name="Launch">How it launches, and whether that route is validated.</param>
/// <param name="Multiplayer">Whether it is known to have multiplayer.</param>
/// <param name="MultiplayerEvidence">Why, in one sentence the preview shows.</param>
/// <param name="IsGame">Whether this is a game rather than an ordinary application.</param>
/// <param name="Notes">Anything else worth showing, such as an unmodelled config element.</param>
/// <param name="Artwork">
///     The official images the catalog offered, best first within each artwork type, empty when it
///     offered none.
/// </param>
/// <param name="Routes">
///     The command routes the title can be launched by, default first, or null for the packaged route
///     that only the Xbox source uses.
/// </param>
/// <param name="Content">Authoritative backing content and stable managed-launch identity, when tracked.</param>
/// <param name="ArtworkQuery">Source-owned artwork lookup metadata, without later UI-thread file checks.</param>
public sealed record DiscoveredGame(
    string SourceId,
    string Key,
    string Name,
    string InstallPath,
    GameLaunch Launch,
    MultiplayerVerdict Multiplayer,
    string MultiplayerEvidence,
    bool IsGame,
    IReadOnlyList<string> Notes,
    IReadOnlyList<DiscoveredArtwork> Artwork,
    IReadOnlyList<ShortcutRoute>? Routes = null,
    ManagedContentRecord? Content = null,
    LibraryArtworkQuery? ArtworkQuery = null)
{
    /// <summary>The command routes, empty for a packaged title.</summary>
    public IReadOnlyList<ShortcutRoute> CommandRoutes => Routes ?? [];

    /// <summary>Whether the title launches through the packaged launcher rather than a command.</summary>
    public bool Packaged => CommandRoutes.Count == 0;

    /// <summary>A game a launcher source found, launched by command routes.</summary>
    /// <param name="sourceId">Which source found it.</param>
    /// <param name="key">Its stable identity within that source.</param>
    /// <param name="name">What to call it in the library.</param>
    /// <param name="installPath">Where it is installed.</param>
    /// <param name="routes">Its routes, default first; at least one.</param>
    /// <param name="content">Authoritative managed content for the managed route, or null for a title without one.</param>
    /// <param name="wrapManagedLaunch">Whether this one explicit route uses managed launch indirection.</param>
    /// <param name="artworkQuery">Source-owned provider and content identification.</param>
    /// <returns>
    ///     The game, launched as its first route says. Launchers do not say whether a game has
    ///     multiplayer or offer artwork, so neither is claimed.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="routes" /> is empty.</exception>
    public static DiscoveredGame Command(
        string sourceId, string key, string name, string installPath, IReadOnlyList<ShortcutRoute> routes,
        ManagedContentRecord? content = null, bool wrapManagedLaunch = false,
        LibraryArtworkQuery? artworkQuery = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0)
        {
            throw new ArgumentException("A command-launched game has at least one route.", nameof(routes));
        }

        if (wrapManagedLaunch && content is not null)
        {
            if (routes.Count != 1)
            {
                throw new ArgumentException("A managed route has one authoritative launch plan.", nameof(routes));
            }

            routes = [routes[0] with { Id = "managed", ManagedId = content.Id }];
        }

        return new DiscoveredGame(
            sourceId,
            key,
            name,
            installPath,
            new GameLaunch(routes[0].Label, true, routes[0].Evidence),
            MultiplayerVerdict.Unknown,
            "The launcher does not say.",
            true,
            [],
            [],
            routes,
            content,
            artworkQuery ?? new LibraryArtworkQuery());
    }

    /// <summary>Builds complete source metadata with the adapter's own friendly library label.</summary>
    public static DiscoveredGame Command(ILibrarySource source, string key, string name, string installPath,
        IReadOnlyList<ShortcutRoute> routes, ManagedContentRecord? content = null, bool wrapManagedLaunch = false,
        LibraryArtworkQuery? artworkQuery = null)
    {
        return Command(source.Id, key, name, installPath, routes, content, wrapManagedLaunch, artworkQuery);
    }
}
