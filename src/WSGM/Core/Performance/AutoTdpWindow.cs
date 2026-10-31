namespace WSGM.Core;

/// <summary>One RTSS measurement window, exactly as the shared memory reported it.</summary>
/// <param name="StartTicks">RTSS <c>dwTime0</c>, the tick the window started at.</param>
/// <param name="EndTicks">RTSS <c>dwTime1</c>, the tick it ended at.</param>
/// <param name="Frames">Frames presented inside it.</param>
/// <param name="MeanFrametimeMs">Its mean frametime.</param>
/// <param name="LastFrameMs">The most recent frame's own time, when RTSS reported one.</param>
/// <param name="AgeMs">How long ago the window ended, as of the read.</param>
/// <remarks>
///     The window bounds are its identity. Two reads that return the same bounds read one measurement
///     twice, and the controller must not count it twice; the growing age of a window that will not
///     close is how a blocked render thread announces itself before its own window exists.
/// </remarks>
internal sealed record AutoTdpWindow(
    uint StartTicks,
    uint EndTicks,
    uint Frames,
    double MeanFrametimeMs,
    double? LastFrameMs,
    double AgeMs)
{
    /// <summary>How long the window covered.</summary>
    internal double DurationMs => EndTicks - StartTicks;

    /// <summary>Whether the window has usable numbers at all.</summary>
    internal bool IsMeasured =>
        double.IsFinite(MeanFrametimeMs) && MeanFrametimeMs > 0 && Frames > 0 && EndTicks > StartTicks;
}
