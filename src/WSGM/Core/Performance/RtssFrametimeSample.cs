namespace WSGM.Core;

/// <summary>One rendering application as RTSS currently reports it.</summary>
/// <param name="ProcessId">The rendering process.</param>
/// <param name="ExecutablePath">Path RTSS recorded for that process.</param>
/// <param name="MeanFrametimeMs">Mean frametime across RTSS's own averaging window.</param>
/// <param name="Frames">Frames in that window.</param>
/// <param name="AgeMs">How long ago the window ended.</param>
/// <param name="WindowStartTicks">RTSS's <c>dwTime0</c>: low 32 bits of the tick count the window started at.</param>
/// <param name="WindowEndTicks">RTSS's <c>dwTime1</c>: low 32 bits of the tick count the window ended at.</param>
/// <param name="FrameTimeRaw">RTSS's <c>dwFrameTime</c>, unscaled. Its unit is not live-verified.</param>
/// <remarks>
///     The raw window bounds identify the measurement itself. Two reads that return the same bounds read the
///     same window twice, which the AutoTDP trace records instead of letting it pass as fresh evidence.
/// </remarks>
internal sealed record RtssFrametimeSample(
    uint ProcessId,
    string ExecutablePath,
    double MeanFrametimeMs,
    uint Frames,
    long AgeMs,
    uint WindowStartTicks = 0,
    uint WindowEndTicks = 0,
    uint FrameTimeRaw = 0);
