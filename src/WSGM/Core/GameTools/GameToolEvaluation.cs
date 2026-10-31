using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What one tool's page shows for one game, decided from facts already read from disk.</summary>
/// <param name="Status"><c>notInstalled</c>, <c>installed</c>, <c>updateAvailable</c>, <c>modified</c>, <c>conflict</c> or <c>unavailable</c>.</param>
/// <param name="StatusText">A short user-facing status line.</param>
/// <param name="IncompatibleReason">Why the tool can't be installed for the chosen runtime, or null.</param>
/// <param name="Actions">Commands currently valid for this tool.</param>
/// <param name="Conflicts">Files that block installation or need review.</param>
/// <param name="Warnings">Things the user should know before acting.</param>
/// <param name="UpdateChangesConfig">Whether the available update changes the configuration format.</param>
/// <param name="ShowOptions">Whether the file name and settings are shown at all.</param>
/// <param name="CanEditOptions">Whether the file name and settings can be changed now.</param>
internal sealed record GameToolEvaluation(
    string Status,
    string StatusText,
    string? IncompatibleReason,
    IReadOnlyList<string> Actions,
    IReadOnlyList<GameToolIssue> Conflicts,
    IReadOnlyList<string> Warnings,
    bool UpdateChangesConfig,
    bool ShowOptions,
    bool CanEditOptions);
