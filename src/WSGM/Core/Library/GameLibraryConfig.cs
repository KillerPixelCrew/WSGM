using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Which artwork a title starts on before the user picks anything.</summary>
public enum ArtworkPreference
{
    /// <summary>The source's own catalog first, such as the Microsoft Store for Xbox titles.</summary>
    Catalog,

    /// <summary>SteamGridDB and the other artwork providers first.</summary>
    Providers
}

/// <summary>A folder whose shortcuts the Game Library offers as titles.</summary>
public sealed class ShortcutFolderConfig
{
    /// <summary>The file types a shortcuts folder can offer, each with its dot: all a shortcut can be.</summary>
    public static IReadOnlyList<string> AllowedExtensions { get; } = [".lnk", ".url", ".exe"];

    /// <summary>Stable identity of the folder source, <c>folder:</c> and a short token. Never reused.</summary>
    public string Id { get; set; } = "";

    /// <summary>The folder.</summary>
    public string Path { get; set; } = "";

    /// <summary>Whether folders inside it are read too.</summary>
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>
    ///     The file types offered, each with its dot, such as <c>.lnk</c>; a subset of
    ///     <see cref="AllowedExtensions" />.
    /// </summary>
    public List<string> Extensions { get; set; } = [.. AllowedExtensions];

    /// <summary>Copies the folder configuration and its mutable extension list.</summary>
    /// <returns>A detached draft suitable for editing without mutating the current configuration.</returns>
    internal ShortcutFolderConfig Copy()
    {
        var copy = (ShortcutFolderConfig)MemberwiseClone();
        copy.Extensions = [.. Extensions];
        return copy;
    }
}

/// <summary>How the Game Library treats titles it has not been told anything specific about.</summary>
/// <remarks>
///     Persisted as <see cref="AppConfig.GameLibrary" />. The defaults are edited on Settings' Steam
///     page; the sources and folders are ticked and added on the Game Library's own surfaces, because
///     that is where the user sees what each one found. What the user decides about one title is not
///     here: those are the library's stored per-title choices.
/// </remarks>
public sealed class GameLibraryConfig
{
    /// <summary>
    ///     The launch mode a newly found single-player Xbox title starts on. A multiplayer title starts
    ///     controller-only whatever this says, and a title with no validated route can only be
    ///     controller-only.
    /// </summary>
    public ImportMode DefaultMode { get; set; } = ImportMode.SteamIntegration;

    /// <summary>
    ///     Whether titles with no validated launch route are offered at all. They can only ever be
    ///     controller-only, so they are hidden unless asked for.
    /// </summary>
    public bool ImportUnroutable { get; set; }

    /// <summary>Which artwork a title starts on before the user picks anything.</summary>
    public ArtworkPreference ArtworkPreference { get; set; } = ArtworkPreference.Catalog;

    /// <summary>
    ///     The sources the user unticked. Stored as the ones switched off rather than the ones switched
    ///     on, so a launcher installed later is read without anyone having to find and tick it.
    /// </summary>
    public List<string> DisabledSources { get; set; } = [];

    /// <summary>The shortcuts folders the user added.</summary>
    public List<ShortcutFolderConfig> ShortcutFolders { get; set; } = [];

    /// <summary>
    ///     Whether each source's imported titles are kept in a Steam collection named after it: one per
    ///     launcher and one per shortcuts folder. Off leaves the collections already made as they are.
    /// </summary>
    public bool CreateCollections { get; set; }

    /// <summary>Copies the library settings, source list and folder drafts.</summary>
    /// <returns>A detached configuration draft with independent mutable lists.</returns>
    internal GameLibraryConfig Copy()
    {
        var copy = (GameLibraryConfig)MemberwiseClone();
        copy.DisabledSources = [.. DisabledSources];
        copy.ShortcutFolders = [.. ShortcutFolders.Select(folder => folder.Copy())];
        return copy;
    }
}
