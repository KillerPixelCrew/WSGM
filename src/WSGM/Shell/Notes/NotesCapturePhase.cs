namespace WSGM.Shell;

/// <summary>Where a screenshot transaction is.</summary>
/// <remarks>
///     Armed → Countdown → Capturing → Restoring → Idle. Cancellation, game exit, a lost foreground, a failed
///     capture and shutdown leave from any phase; every exit hides the countdown, and every exit except
///     shutdown returns the player to the same page.
/// </remarks>
internal enum NotesCapturePhase
{
    /// <summary>No screenshot is pending.</summary>
    Idle,

    /// <summary>Validated and returning the player to the game.</summary>
    Armed,

    /// <summary>The game is in front and the countdown is running.</summary>
    Countdown,

    /// <summary>The countdown expired; the game's window is being captured.</summary>
    Capturing,

    /// <summary>The image is in the page and the Notes surface is reopening.</summary>
    Restoring
}
