namespace WSGM.Shell;

/// <summary>The result of one entry attempt.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Warning">
///     User-facing warning, or null when there is nothing to say. Also null when the desktop return
///     failed, because the return has already shown the pending-desktop warning.
/// </param>
internal sealed record GameModeEntryResult(GameModeEntryOutcome Outcome, string? Warning = null);
