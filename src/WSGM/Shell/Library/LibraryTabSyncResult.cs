namespace WSGM.Shell;

/// <summary>Structured outcome for tab synchronization and retry policy.</summary>
/// <param name="Summary">User-facing summary.</param>
/// <param name="Success">Whether the tab definitions synchronized.</param>
/// <param name="Reading">
///     The configuration the sync saved after merging its drive scan, with the cards that scan found
///     mounted, or null when the sync ended before saving one.
/// </param>
internal readonly record struct LibraryTabSyncResult(string Summary, bool Success, LibraryReading? Reading = null);
