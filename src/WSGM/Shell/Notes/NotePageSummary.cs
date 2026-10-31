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
