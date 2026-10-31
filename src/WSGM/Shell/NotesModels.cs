using System;

namespace WSGM.Shell;

/// <summary>One page as a notebook lists it.</summary>
/// <param name="Id">The page id.</param>
/// <param name="Title">Its title, or a placeholder for an untitled page.</param>
/// <param name="Pinned">Whether it is pinned.</param>
/// <param name="Modified">When its content last changed.</param>
/// <param name="Revision">Its content revision.</param>
/// <param name="ReadOnly">Written by a newer WSGM; shown, never changed.</param>
/// <param name="Preview">The start of its first text block.</param>
/// <param name="Texts">Number of text blocks.</param>
/// <param name="Images">Number of images.</param>
/// <param name="Strokes">Number of ink strokes.</param>
internal sealed record NotePageSummary(
    string Id,
    string Title,
    bool Pinned,
    DateTimeOffset Modified,
    long Revision,
    bool ReadOnly,
    string Preview,
    int Texts,
    int Images,
    int Strokes);

/// <summary>One notebook.</summary>
/// <param name="Scope">Its identity.</param>
/// <param name="Title">The game's name, or "Global".</param>
/// <param name="Pages">How many pages it has.</param>
internal sealed record NotebookSummary(string Scope, string Title, int Pages);

/// <summary>What the running game resolves to for Notes.</summary>
internal enum NotesGameState
{
    /// <summary>A game is running and has a stable identity.</summary>
    Game,

    /// <summary>No game is running.</summary>
    None,

    /// <summary>Something is running but its identity is unknown or ambiguous right now.</summary>
    Unknown
}

/// <summary>The running game as Notes sees it.</summary>
/// <param name="State">Whether there is one.</param>
/// <param name="Scope">Its notebook, for <see cref="NotesGameState.Game" />.</param>
/// <param name="Title">Its display name.</param>
/// <param name="Detail">Why the identity is unknown, for <see cref="NotesGameState.Unknown" />.</param>
internal sealed record NotesCurrentGame(NotesGameState State, string? Scope, string Title, string? Detail = null)
{
    internal static NotesCurrentGame NoGame { get; } = new(NotesGameState.None, null, "No game running");
}

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

/// <summary>Text the overlay had typed but not yet saved, kept when the overlay closes mid-edit.</summary>
/// <param name="PageId">The page.</param>
/// <param name="ElementId">The text block, or null for a new block.</param>
/// <param name="BaseRevision">The revision the edit started from.</param>
/// <param name="Text">The unsaved text.</param>
/// <param name="X">Where a new block goes.</param>
/// <param name="Y">Where a new block goes.</param>
internal sealed record NotesTextDraft(string PageId, string? ElementId, long BaseRevision, string Text, double X, double Y);
