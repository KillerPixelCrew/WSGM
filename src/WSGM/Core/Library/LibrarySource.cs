using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Source-produced artwork identification, captured away from UI publication.</summary>
/// <param name="ProviderId">The preferred provider for this source.</param>
/// <param name="PlatformId">The provider's platform identity, or zero when unmapped.</param>
/// <param name="ContentName">The original filename, when the provider indexes backing content.</param>
/// <param name="ContentSize">The observed file size, or zero when unavailable.</param>
public sealed record LibraryArtworkQuery(
    string ProviderId = "steamgriddb",
    int PlatformId = 0,
    string ContentName = "",
    long ContentSize = 0);

/// <summary>Whether a title is known to have multiplayer, which decides its default input mode.</summary>
public enum MultiplayerVerdict
{
    /// <summary>Nothing said either way. Treated as single-player for the default.</summary>
    Unknown,

    /// <summary>No multiplayer capability was reported.</summary>
    SinglePlayer,

    /// <summary>A multiplayer capability was reported.</summary>
    Multiplayer
}

/// <summary>One official image a source's catalog offers for a title.</summary>
/// <param name="Asset">Which Steam capsule it fills.</param>
/// <param name="Url">Where to fetch it, always HTTPS.</param>
/// <remarks>
///     Carried from discovery rather than looked up again at apply time, because the one catalog
///     response that answers "is this a game?" and "does it have multiplayer?" carries the images
///     too, and the catalog is paced at one request every 500 ms.
/// </remarks>
public sealed record DiscoveredArtwork(ArtworkAsset Asset, string Url);

/// <summary>How a source says one of its games launches, as far as importing it is concerned.</summary>
/// <param name="Label">What to call the route on screen.</param>
/// <param name="Validated">
///     Whether a validated route exists. Without one, Steam integration is never offered, because
///     there is nothing an acknowledgement could authorise.
/// </param>
/// <param name="Evidence">Why, in one sentence the review shows.</param>
/// <remarks>
///     This is the source's evidence, not a launch argument. The shortcut carries only the title's
///     key and the chosen mode; the launcher decides the route again from the process it actually
///     starts, because a package can change after its shortcut was written.
/// </remarks>
public sealed record GameLaunch(string Label, bool Validated, string Evidence);

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
    /// <param name="content">Explicit authoritative managed content, or null to derive command-source metadata.</param>
    /// <param name="wrapManagedLaunch">Whether this one explicit route uses managed launch indirection.</param>
    /// <param name="artworkQuery">Source-owned provider and content identification.</param>
    /// <param name="retainContent">Whether this source participates in backing-content availability.</param>
    /// <param name="location">The adapter-defined friendly library label.</param>
    /// <returns>
    ///     The game, launched as its first route says. Launchers do not say whether a game has
    ///     multiplayer or offer artwork, so neither is claimed.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="routes" /> is empty.</exception>
    public static DiscoveredGame Command(
        string sourceId, string key, string name, string installPath, IReadOnlyList<ShortcutRoute> routes,
        ManagedContentRecord? content = null, bool wrapManagedLaunch = false,
        LibraryArtworkQuery? artworkQuery = null, bool retainContent = true, string location = "")
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0)
        {
            throw new ArgumentException("A command-launched game has at least one route.", nameof(routes));
        }

        if (retainContent && content is null && installPath.Length > 0 && Path.IsPathFullyQualified(installPath))
        {
            content = ManagedContentStorage.CreateRecord(sourceId, key, name, LibrarySourceKind.Launcher,
                location.Length > 0 ? location : sourceId, installPath, true);
            content.LaunchKind = ManagedLaunchKind.Native;
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
        LibraryArtworkQuery? artworkQuery = null, bool retainContent = true)
    {
        return Command(source.Id, key, name, installPath, routes, content, wrapManagedLaunch, artworkQuery,
            retainContent, source.DisplayName);
    }
}

/// <summary>Whether a source's launcher is on this machine.</summary>
/// <param name="Installed">Whether it was found.</param>
/// <param name="Detail">What was found or why not, in one short phrase the sidebar shows.</param>
public sealed record SourceAvailability(bool Installed, string Detail)
{
    /// <summary>A launcher that is not installed.</summary>
    public static SourceAvailability NotFound { get; } = new(false, "Not found");
}

/// <summary>One launcher whose installed games the Game Library can bring into Steam.</summary>
/// <remarks>
///     <para>
///         The spine of the Game Library, in the sense Steam ROM Manager's parsers are the spine of
///         that tool: each source knows how to find one launcher's games and describe them in the
///         shape above, and everything after discovery - planning, the user's choices, review,
///         writing the shortcut, artwork - is shared and knows nothing about where a game came from.
///     </para>
///     <para>
///         A source's identity has to stay stable across releases, because every record and every
///         stored choice is keyed by it.
///     </para>
///     <para>
///         Windows' installed-programs list is read once per scan by the caller and handed to every
///         source, because most launchers are found through their uninstall entries and each read walks
///         hundreds of registry keys in four places.
///     </para>
///     <para>
///         A source that cannot read its launcher's data throws from <see cref="DiscoverAsync" />
///         rather than answering with nothing: an empty answer reads as every title having been
///         uninstalled.
///     </para>
/// </remarks>
public interface ILibrarySource
{
    /// <summary>The adapter category, so policy is never inferred from an opaque source id.</summary>
    LibrarySourceKind Kind => LibrarySourceKind.Launcher;

    /// <summary>Stable identity of this source, lower case, never reused.</summary>
    string Id { get; }

    /// <summary>What to call it in the UI.</summary>
    string DisplayName { get; }

    /// <summary>What to call the images this source's own catalog offers, on screen.</summary>
    /// <remarks>
    ///     The source's name, unless its images come from somewhere else, as the Xbox source's come from
    ///     the Store.
    /// </remarks>
    string CatalogName => DisplayName;

    /// <summary>Whether the launcher is installed. Cheap: registry and file checks only.</summary>
    /// <param name="programs">Windows' installed-programs list, read once for the whole scan.</param>
    /// <returns>What was found.</returns>
    SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs);

    /// <summary>Finds everything this source can offer.</summary>
    /// <param name="programs">The same installed-programs list detection was given.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What was found, in no particular order.</returns>
    Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken);
}
