namespace WSGM.Overlay;

/// <summary>What a level's live progress element shows: the phase line, the bar and the Cancel command.</summary>
/// <param name="Text">The phase and amount, such as "Downloading… · 4 MB of 20 MB"; empty shows no line.</param>
/// <param name="Active">Whether work is running, which shows the bar.</param>
/// <param name="Fraction">Completed share from 0 to 1, or null for an indeterminate bar.</param>
/// <param name="Cancel">The Cancel command while the work can still be stopped, or null.</param>
internal sealed record OverlayProgressState(
    string Text,
    bool Active,
    double? Fraction = null,
    OverlayProgressCancel? Cancel = null);
