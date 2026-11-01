namespace WSGM.Core;

/// <summary>A read-only view of the controller's evidence, for the AutoTDP trace.</summary>
/// <param name="ContextKey">The context the controller is judging.</param>
/// <param name="Watts">The limit the controller believes is in effect.</param>
/// <param name="LastGood">The limit in effect before the current probe.</param>
/// <param name="Phase">Where the control loop is.</param>
/// <param name="IsPaused">Whether a manual change suspended control.</param>
/// <param name="Class">What the last window was classified as.</param>
/// <param name="Ratio">Frametime over deadline for the last judged window, or NaN.</param>
/// <param name="GapMs">Time since the last fresh window ended.</param>
/// <param name="Hiatus">Whether a present hiatus is in progress.</param>
/// <param name="MissedWindows">Consecutive missed windows counted toward a raise.</param>
/// <param name="DwellMs">Stable window time accumulated toward a downward probe.</param>
/// <param name="RequiredDwellMs">Stable window time this probe currently needs.</param>
/// <param name="RaiseBaseline">The ratio the current raise chain is judged against, or NaN.</param>
/// <param name="RaiseUnimproved">Steps in this chain that delivery did not answer.</param>
/// <param name="ProbeWindows">Windows the current probe has passed.</param>
/// <param name="SettlingWindows">Windows still ignored after the last write.</param>
/// <param name="SevereWindows">Consecutive severe windows inside a quarantine.</param>
/// <param name="Utilization">Which utilization rule last changed an outcome, or null.</param>
internal readonly record struct AutoTdpControllerSnapshot(
    string ContextKey,
    int Watts,
    int LastGood,
    AutoTdpPhase Phase,
    bool IsPaused,
    AutoTdpWindowClass Class,
    double Ratio,
    double GapMs,
    bool Hiatus,
    int MissedWindows,
    double DwellMs,
    double RequiredDwellMs,
    double RaiseBaseline,
    int RaiseUnimproved,
    int ProbeWindows,
    int SettlingWindows,
    int SevereWindows,
    string? Utilization);
