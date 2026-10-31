namespace WSGM.Shell;

/// <summary>The screenshot state as Steam's Notes page shows it.</summary>
internal sealed record NotesSteamCapture(
    string Phase,
    string Origin,
    string? PageId,
    int Remaining,
    string? Outcome,
    bool OutcomeIsError,
    string? InsertedElementId);
