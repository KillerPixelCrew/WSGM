using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

internal enum DeviceDesiredWriteSkipReason
{
    Unsupported,
    MissingDesiredValue,
    MissingDesiredSource,
    Unavailable,
    DesiredValueOutOfRange,
    UntrustedState,
    AlreadyApplied,
    CommandPending,
    PreviousResultUncertain
}

/// <summary>The shared admission decision for automatically restoring a desired device value.</summary>
internal readonly record struct DeviceDesiredWriteAdmission(
    CapabilityValue? DesiredValue,
    DeviceDesiredWriteSkipReason? SkipReason)
{
    internal bool Admitted => SkipReason is null;

    internal static DeviceDesiredWriteAdmission TryAdmit(DeviceCapabilityView view)
    {
        var projection = view.Projection;
        if (!view.Descriptor.SupportsWrite)
        {
            return Skipped(DeviceDesiredWriteSkipReason.Unsupported);
        }

        if (projection.DesiredValue is not { } desired)
        {
            return Skipped(DeviceDesiredWriteSkipReason.MissingDesiredValue);
        }

        if (projection.DesiredSource is ProfileSource.None)
        {
            return Skipped(DeviceDesiredWriteSkipReason.MissingDesiredSource);
        }

        if (!projection.State.Available)
        {
            return Skipped(DeviceDesiredWriteSkipReason.Unavailable);
        }

        if (projection.DesiredValueOutOfRange)
        {
            return Skipped(DeviceDesiredWriteSkipReason.DesiredValueOutOfRange);
        }

        // A value that was never read back is still restored; only an expired or faulted state is not.
        if (!DeviceCapabilityRouter.CanCommand(projection.State))
        {
            return Skipped(DeviceDesiredWriteSkipReason.UntrustedState);
        }

        if (projection.State.ObservedValue is { } observed && DeviceCoordinator.SameValue(observed, desired))
        {
            return Skipped(DeviceDesiredWriteSkipReason.AlreadyApplied);
        }

        if (projection.PendingValue is not null)
        {
            return Skipped(DeviceDesiredWriteSkipReason.CommandPending);
        }

        // An uncertain write may already have happened, so it is never simply retried. A readback
        // taken after it finished is the re-read that settles it: the device reports a value other
        // than the desired one (a match returned AlreadyApplied above), so the write did not stick.
        if (view.LastResult is { Outcome: CommandOutcome.Indeterminate or CommandOutcome.TimedOut } uncertain
            && !(projection.State.ObservedAt is { } observedAt && observedAt > uncertain.CompletedAt))
        {
            return Skipped(DeviceDesiredWriteSkipReason.PreviousResultUncertain);
        }

        return new DeviceDesiredWriteAdmission(desired, null);
    }

    private static DeviceDesiredWriteAdmission Skipped(DeviceDesiredWriteSkipReason reason)
    {
        return new DeviceDesiredWriteAdmission(null, reason);
    }
}
