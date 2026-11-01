using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What <see cref="NoteStore.Load" /> found.</summary>
/// <param name="Pages">Every readable page.</param>
/// <param name="Settings">The settings, or defaults when there were none.</param>
/// <param name="SetAside">Files that did not parse and were moved aside, for the log.</param>
/// <param name="ReadOnlyPages">Pages written by a newer WSGM; listed but never rewritten.</param>
public sealed record NoteStoreSnapshot(
    IReadOnlyList<NotePage> Pages,
    NoteSettings Settings,
    IReadOnlyList<string> SetAside,
    IReadOnlySet<string> ReadOnlyPages);
