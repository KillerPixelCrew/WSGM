namespace WSGM.Shell;

/// <summary>Text the overlay had typed but not yet saved, kept when the overlay closes mid-edit.</summary>
/// <param name="PageId">The page.</param>
/// <param name="ElementId">The text block, or null for a new block.</param>
/// <param name="BaseRevision">The revision the edit started from.</param>
/// <param name="Text">The unsaved text.</param>
/// <param name="X">Where a new block goes.</param>
/// <param name="Y">Where a new block goes.</param>
internal sealed record NotesTextDraft(string PageId, string? ElementId, long BaseRevision, string Text, double X, double Y);
