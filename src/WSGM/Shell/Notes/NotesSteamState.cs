using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Everything Steam's Notes page draws.</summary>
/// <param name="Loaded">Whether the store has been read; false until the page first opens.</param>
/// <param name="Error">Why the store is unusable.</param>
/// <param name="Notice">A one-line notice.</param>
/// <param name="Game">The game this overlay belongs to.</param>
/// <param name="Scope">The notebook shown.</param>
/// <param name="ScopeTitle">Its name.</param>
/// <param name="Notebooks">Every notebook, for switching.</param>
/// <param name="Pages">The shown notebook's pages.</param>
/// <param name="Page">The open page, or null for the list.</param>
/// <param name="PageReadOnly">Whether the open page is read-only.</param>
/// <param name="Capture">The screenshot state.</param>
/// <param name="Countdown">The countdown the next screenshot offers.</param>
/// <param name="CaptureAvailable">Whether this page runs in a game's overlay, where a screenshot can be taken.</param>
internal sealed record NotesSteamState(
    bool Loaded,
    string? Error,
    string? Notice,
    NotesSteamGame Game,
    string Scope,
    string ScopeTitle,
    IReadOnlyList<NotebookSummary> Notebooks,
    IReadOnlyList<NotePageSummary> Pages,
    NotePage? Page,
    bool PageReadOnly,
    NotesSteamCapture Capture,
    int Countdown,
    bool CaptureAvailable);
