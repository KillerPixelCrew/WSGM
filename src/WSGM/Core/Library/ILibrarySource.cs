using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

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
