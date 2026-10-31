namespace WSGM.Shell;

/// <summary>The current screenshot state, as both surfaces show it.</summary>
/// <param name="Phase">Where the transaction is.</param>
/// <param name="Origin">The surface it started from.</param>
/// <param name="PageId">The page the image goes into.</param>
/// <param name="Seconds">The chosen countdown.</param>
/// <param name="Remaining">Seconds left while counting down.</param>
/// <param name="Outcome">How the last transaction ended, until the next one starts.</param>
/// <param name="OutcomeIsError">Whether that outcome is a failure.</param>
/// <param name="InsertedElementId">The image element the last successful capture added.</param>
internal sealed record NotesCaptureStatus(
    NotesCapturePhase Phase,
    NotesCaptureOrigin Origin,
    string? PageId,
    int Seconds,
    int Remaining,
    string? Outcome,
    bool OutcomeIsError,
    string? InsertedElementId = null)
{
    /// <summary>Nothing pending and nothing to report.</summary>
    internal static NotesCaptureStatus None { get; } =
        new(NotesCapturePhase.Idle, NotesCaptureOrigin.Overlay, null, 0, 0, null, false);
}
