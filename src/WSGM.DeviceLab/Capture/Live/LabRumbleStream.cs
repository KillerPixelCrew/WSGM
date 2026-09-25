using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.DeviceLab.Capture.Live;

/// <summary>
///     Streams the slider page's current strengths to the motors, so they rumble live while the tester
///     moves a slider.
/// </summary>
/// <remarks>
///     The current frame is written every <see cref="IntervalMilliseconds" /> while it is not zero, and
///     once when it changes. A frame left unchanged for <see cref="IdleStopMilliseconds" /> is replaced by
///     zero, so a forgotten slider cannot rumble forever. When the stream stops, for any reason, it writes
///     an explicit zero. A synchronous write failure ends the stream and is never retried. The
///     worker's one-way slider frames are checked against worker evidence after the stream stops.
/// </remarks>
internal sealed class LabRumbleStream
{
    /// <summary>Time between writes, in milliseconds.</summary>
    public const int IntervalMilliseconds = 50;

    /// <summary>How long an unchanged, non-zero frame keeps rumbling, in milliseconds.</summary>
    public const int IdleStopMilliseconds = 8000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private readonly ILabRumbleOutput _output;
    private TimeSpan _changedAt;
    private LabRumbleFrame _target = LabRumbleFrame.Zero;

    /// <summary>Creates a stream on an opened route; nothing is written until <see cref="Run" />.</summary>
    /// <param name="output">The route.</param>
    public LabRumbleStream(ILabRumbleOutput output)
    {
        _output = output;
    }

    /// <summary>When the stream started.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>When the stream stopped.</summary>
    public DateTimeOffset? StoppedAt { get; private set; }

    /// <summary>How often an unchanged frame was switched off after <see cref="IdleStopMilliseconds" />.</summary>
    public int IdleStops { get; private set; }

    /// <summary>The write failure that ended the stream, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether the final zero failed.</summary>
    public bool ZeroFailed { get; private set; }

    /// <summary>Sets the strengths to stream next.</summary>
    /// <param name="frame">Strengths, each 0 to 100 percent.</param>
    public void Set(LabRumbleFrame frame)
    {
        frame.Checked();
        lock (_gate)
        {
            _target = frame;
            _changedAt = _clock.Elapsed;
        }
    }

    /// <summary>Streams until cancelled or until a write fails, then writes a zero.</summary>
    /// <param name="cancel">Stops the stream.</param>
    /// <returns>A task that completes once the final zero has been written.</returns>
    public Task Run(CancellationToken cancel)
    {
        return Task.Run(() => Loop(cancel), CancellationToken.None);
    }

    private void Loop(CancellationToken cancel)
    {
        StartedAt = DateTimeOffset.UtcNow;
        var written = LabRumbleFrame.Zero;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                LabRumbleFrame target;
                lock (_gate)
                {
                    if (!_target.IsZero &&
                        _clock.Elapsed - _changedAt > TimeSpan.FromMilliseconds(IdleStopMilliseconds))
                    {
                        _target = LabRumbleFrame.Zero;
                        IdleStops++;
                    }

                    target = _target;
                }

                if (!target.IsZero || target != written)
                {
                    _output.Write(target, "stream");
                    written = target;
                }

                cancel.WaitHandle.WaitOne(IntervalMilliseconds);
            }
        }
        catch (LabRumbleWriteException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            try
            {
                _output.Write(LabRumbleFrame.Zero, "stream-stop");
            }
            catch (LabRumbleWriteException ex)
            {
                Error ??= ex.Message;
                ZeroFailed = true;
            }

            StoppedAt = DateTimeOffset.UtcNow;
        }
    }
}
