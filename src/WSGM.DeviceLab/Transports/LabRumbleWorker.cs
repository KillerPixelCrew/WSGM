using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Capture.Live;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Transports;

/// <summary>The bounded motor output exposed by the hardware worker.</summary>
internal interface ILabRumbleWorker : IDisposable
{
    /// <summary>Captures the operator's silent baseline before a motor write.</summary>
    [LabWorkerSnapshot]
    string Original();

    /// <summary>Writes one bounded frame and returns its evidence, including a failed write.</summary>
    [LabWorkerWrite]
    LabRumbleWrite Write(LabRumbleFrame frame, string purpose);

    /// <summary>Updates the live slider without waiting for a per-frame acknowledgement.</summary>
    [LabWorkerStream]
    void SetIntensity(LabRumbleFrame frame);

    /// <summary>Returns the worker's streamed frame evidence after the slider stops.</summary>
    IReadOnlyList<LabRumbleWrite> StreamWrites();

    /// <summary>Sends a final zero, also used when the worker closes the service.</summary>
    [LabWorkerZero]
    void Zero();
}

/// <summary>The rumble route is not present now, so nothing on it can be driving a motor.</summary>
/// <param name="message">What happened.</param>
internal sealed class LabRumbleRouteGoneException(string message) : InvalidOperationException(message);

/// <summary>Opens only a route rediscovered from the confirmed knowledge record inside the worker.</summary>
/// <remarks>
///     The close-time and watchdog <see cref="Zero" /> is skipped only after a zero was written
///     successfully. A failed zero leaves the motor state unknown, so the safety zero still runs; zero is
///     the safe direction, so sending it again is not a retry of an uncertain write. A failed safety zero
///     is reported as such, apart from ordinary write failures.
/// </remarks>
internal sealed class LabRumbleWorker : ILabRumbleWorker
{
    private readonly ILabRumbleOutput _output;
    private readonly LabRumbleLog _writes = new();
    private bool _zeroed;

    private LabRumbleWorker(string? recordId, string routeId, string target)
    {
        var record = DeviceKnowledgeBase.Default.Records.FirstOrDefault(item => item.Id == recordId);
        var routes = LabRumbleRoutes.Discover(record).Routes;
        var route = routes.SingleOrDefault(item => item.Id == routeId && item.Target == target)
                    ?? throw new LabRumbleRouteGoneException("The selected rumble route is no longer present.");
        _output = LabRumbleRoutes.Open(route, _writes);
    }

    // For tests: opens an output that logs into this worker's evidence.
    internal LabRumbleWorker(Func<LabRumbleLog, ILabRumbleOutput> open)
    {
        _output = open(_writes);
    }

    /// <summary>The worker service registration.</summary>
    public static LabWorkerService Service { get; } = new("rumble", typeof(ILabRumbleWorker),
        (args, _) => new LabRumbleWorker(
            LabWorkerService.Arg<string?>(args, 0),
            LabWorkerService.Arg<string>(args, 1),
            LabWorkerService.Arg<string>(args, 2)));

    /// <inheritdoc />
    public void Dispose()
    {
        _output.Dispose();
    }

    /// <inheritdoc />
    public string Original()
    {
        return "Motor state cannot be read; cleanup sends zero output.";
    }

    /// <inheritdoc />
    public LabRumbleWrite Write(LabRumbleFrame frame, string purpose)
    {
        _zeroed = false;
        try
        {
            _output.Write(frame.Checked(), purpose);
            _zeroed = frame.IsZero;
        }
        catch (LabRumbleWriteException)
        {
            // Return the failed write as evidence; the parent never retries it.
        }

        return _writes.Snapshot().Last();
    }

    /// <inheritdoc />
    public void SetIntensity(LabRumbleFrame frame)
    {
        _zeroed = false;
        _output.Write(frame.Checked(), frame.IsZero ? "stream-stop" : "stream");
        _zeroed = frame.IsZero;
    }

    /// <inheritdoc />
    public IReadOnlyList<LabRumbleWrite> StreamWrites()
    {
        return [.. _writes.Snapshot().Where(write => write.Purpose is "stream" or "stream-stop" or "worker-zero")];
    }

    /// <inheritdoc />
    public void Zero()
    {
        if (_zeroed)
        {
            return;
        }

        try
        {
            _output.Write(LabRumbleFrame.Zero, "worker-zero");
        }
        catch (LabRumbleWriteException ex)
        {
            throw new LabRumbleWriteException(
                $"The safety zero failed, so the motors may still be running: {ex.Message}");
        }

        _zeroed = true;
    }
}
