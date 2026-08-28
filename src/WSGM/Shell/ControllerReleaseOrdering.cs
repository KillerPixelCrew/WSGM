using System;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.Shell;

internal enum ControllerReleaseBoundary
{
    NotStarted,
    RoutingStopped,
    OutputStopped,
    TargetNeutralized,
    PhysicalReleaseRequested,
    PhysicalReleaseCompleted,
    PhysicalReleaseTimedOut,
    TargetRemoved,
    HidHideOwnedDeltasRemoved,
    Completed,
}

internal sealed class ControllerReleaseOrder
{
    internal ControllerReleaseBoundary Boundary { get; private set; }

    internal bool HidHideMustRemain => Boundary is >= ControllerReleaseBoundary.RoutingStopped
        and < ControllerReleaseBoundary.TargetRemoved;

    internal bool CanRemoveTarget => Boundary is ControllerReleaseBoundary.PhysicalReleaseCompleted
        or ControllerReleaseBoundary.PhysicalReleaseTimedOut;

    internal bool CanRemoveHidHide => Boundary is ControllerReleaseBoundary.TargetRemoved;

    internal ControllerHandoffResult Result { get; private set; } =
        ControllerHandoffResult.InProgress;

    internal void Advance(ControllerReleaseBoundary next)
    {
        if (!Allowed(Boundary, next))
        {
            throw new InvalidOperationException(
                $"Controller release cannot advance from {Boundary} to {next}.");
        }

        Boundary = next;
        if (next is ControllerReleaseBoundary.Completed)
        {
            Result = _topologyVerified && !_timedOut
                ? ControllerHandoffResult.ReleasedVerified
                : ControllerHandoffResult.ReleasedUnverified;
        }
    }

    internal void RecordPhysicalRelease(bool topologyVerified)
    {
        Advance(ControllerReleaseBoundary.PhysicalReleaseCompleted);
        _topologyVerified = topologyVerified;
    }

    internal void RecordPhysicalReleaseTimeout()
    {
        Advance(ControllerReleaseBoundary.PhysicalReleaseTimedOut);
        _timedOut = true;
    }

    private bool _topologyVerified;
    private bool _timedOut;

    private static bool Allowed(
        ControllerReleaseBoundary current,
        ControllerReleaseBoundary next) => (current, next) switch
        {
            (ControllerReleaseBoundary.NotStarted,
                ControllerReleaseBoundary.RoutingStopped) => true,
            (ControllerReleaseBoundary.RoutingStopped,
                ControllerReleaseBoundary.OutputStopped) => true,
            (ControllerReleaseBoundary.OutputStopped,
                ControllerReleaseBoundary.TargetNeutralized) => true,
            (ControllerReleaseBoundary.TargetNeutralized,
                ControllerReleaseBoundary.PhysicalReleaseRequested) => true,
            (ControllerReleaseBoundary.PhysicalReleaseRequested,
                ControllerReleaseBoundary.PhysicalReleaseCompleted) => true,
            (ControllerReleaseBoundary.PhysicalReleaseRequested,
                ControllerReleaseBoundary.PhysicalReleaseTimedOut) => true,
            (ControllerReleaseBoundary.PhysicalReleaseCompleted,
                ControllerReleaseBoundary.TargetRemoved) => true,
            (ControllerReleaseBoundary.PhysicalReleaseTimedOut,
                ControllerReleaseBoundary.TargetRemoved) => true,
            (ControllerReleaseBoundary.TargetRemoved,
                ControllerReleaseBoundary.HidHideOwnedDeltasRemoved) => true,
            (ControllerReleaseBoundary.HidHideOwnedDeltasRemoved,
                ControllerReleaseBoundary.Completed) => true,
            _ => false,
        };
}
