namespace WSGM.Core;

/// <summary>What a game's page shows and allows for one add-on.</summary>
/// <param name="Show">Whether the row is shown: installed, supported, or the support list couldn't be read.</param>
/// <param name="Support"><c>supported</c>, <c>unsupported</c> or <c>unknown</c> for the game's program.</param>
/// <param name="Installed">Whether WSGM installed it with the tool.</param>
/// <param name="Recommended">Whether a fresh install includes it unless the user turns it off.</param>
/// <param name="CanChange">Whether it can be added or removed now.</param>
/// <param name="Text">The status line: what is installed, what it supports, or why that is unknown.</param>
/// <param name="Note">A vendor note, or null.</param>
/// <param name="UpdateAvailable">Whether a newer build than the installed one is published.</param>
internal sealed record GameToolAddonEvaluation(
    bool Show,
    string Support,
    bool Installed,
    bool Recommended,
    bool CanChange,
    string Text,
    string? Note,
    bool UpdateAvailable);
