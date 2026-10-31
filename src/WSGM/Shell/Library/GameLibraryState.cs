using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Everything either surface renders: the Steam page and the overlay view alike.</summary>
/// <param name="Sources">Every source, in the sidebar's order.</param>
/// <param name="Reading">The names of the sources a scan reads: installed and ticked.</param>
/// <param name="Phase">idle, scanning, review, applying or done.</param>
/// <param name="Entries">What the scan found.</param>
/// <param name="SelectedCount">How many are selected.</param>
/// <param name="Progress">How many entries of an apply are done.</param>
/// <param name="ProgressTotal">How many an apply will do.</param>
/// <param name="LauncherAvailable">Whether the packaged-game launcher is installed.</param>
/// <param name="LauncherDetail">Why it is not, when it is not.</param>
/// <param name="Loading">Whether a scan or an apply is running.</param>
/// <param name="Notice">Something worth saying that is not an error.</param>
/// <param name="Error">Why the last operation did not do what was asked.</param>
/// <param name="ArtworkPreference">Which artwork a title starts on: catalog or providers.</param>
/// <param name="CreateCollections">Whether each source's imported titles are kept in a Steam collection.</param>
/// <param name="Revision">Monotonic publication revision.</param>
/// <param name="RomSources">The configured sources shown by both editors.</param>
/// <param name="ManualSources">The editable user-authored command sources.</param>
/// <param name="StorageLibraries">Every card library known or connected, with its immediate actions.</param>
/// <param name="Shortcuts">Every tracked non-Steam shortcut, WSGM's and the user's, by location and name.</param>
/// <param name="StorageChecked">Whether the tracker has checked Steam's shortcuts at least once.</param>
public sealed record GameLibraryState(
    IReadOnlyList<GameLibrarySource> Sources,
    IReadOnlyList<string> Reading,
    string Phase,
    IReadOnlyList<GameLibraryEntry> Entries,
    int SelectedCount,
    int Progress,
    int ProgressTotal,
    bool LauncherAvailable,
    string? LauncherDetail = null,
    bool Loading = false,
    string? Notice = null,
    string? Error = null,
    string ArtworkPreference = "Catalog",
    bool CreateCollections = false,
    long Revision = 0,
    IReadOnlyList<RomSourceConfig>? RomSources = null,
    IReadOnlyList<ManualShortcutConfig>? ManualSources = null,
    IReadOnlyList<GameLibraryStorage>? StorageLibraries = null,
    IReadOnlyList<ShortcutStatus>? Shortcuts = null,
    bool StorageChecked = false);
