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

/// <summary>Opens only a route rediscovered from the confirmed knowledge record inside the worker.</summary>
internal sealed class LabRumbleWorker : ILabRumbleWorker
{
    private readonly ILabRumbleOutput _output;
    private readonly LabRumbleLog _writes = new();
    private bool _zeroAttempted;

    private LabRumbleWorker(string? recordId, string routeId, string target)
    {
        var record = DeviceKnowledgeBase.Default.Records.FirstOrDefault(item => item.Id == recordId);
        var routes = LabRumbleRoutes.Discover(record).Routes;
        var route = routes.SingleOrDefault(item => item.Id == routeId && item.Target == target)
                    ?? throw new InvalidOperationException("The selected rumble route is no longer present.");
        _output = LabRumbleRoutes.Open(route, _writes);
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
        _zeroAttempted = frame.IsZero;
        try
        {
            _output.Write(frame.Checked(), purpose);
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
        _zeroAttempted = frame.IsZero;
        _output.Write(frame.Checked(), frame.IsZero ? "stream-stop" : "stream");
    }

    /// <inheritdoc />
    public IReadOnlyList<LabRumbleWrite> StreamWrites()
    {
        return [.. _writes.Snapshot().Where(write => write.Purpose is "stream" or "stream-stop" or "worker-zero")];
    }

    /// <inheritdoc />
    public void Zero()
    {
        if (_zeroAttempted)
        {
            return;
        }

        _zeroAttempted = true;
        _output.Write(LabRumbleFrame.Zero, "worker-zero");
    }
}
