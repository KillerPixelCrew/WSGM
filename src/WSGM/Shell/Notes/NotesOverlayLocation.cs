namespace WSGM.Shell;

/// <summary>Where the overlay's Notes was, so a reopened overlay continues there.</summary>
/// <param name="Scope">The notebook shown, or null for the current game's.</param>
/// <param name="PageId">The open page, or null for the list.</param>
/// <param name="Mode">navigate, text or ink.</param>
/// <param name="Zoom">Canvas zoom.</param>
/// <param name="ScrollY">Canvas vertical offset in page units.</param>
internal sealed record NotesOverlayLocation(string? Scope, string? PageId, string Mode, double Zoom, double ScrollY)
{
    internal static NotesOverlayLocation Start { get; } = new(null, null, "navigate", 1, 0);
}
