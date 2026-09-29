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

    /// <remarks>
    ///     A switched capability restores the value the running game resolves to. A global-only or native
    ///     per-application one restores only the Global value: the first has no per-game value at all, and
    ///     for the second the driver applies the game's own value when the game starts.
    /// </remarks>
    internal static DeviceDesiredWriteAdmission TryAdmit(DeviceCapabilityView view)
    {
        var projection = view.Projection;
        if (!view.Descriptor.SupportsWrite)
        {
            return Skipped(DeviceDesiredWriteSkipReason.Unsupported);
        }

        var switched = view.Descriptor.ProfileScope is CapabilityProfileScope.Switched;
        if ((switched ? projection.DesiredValue : projection.GlobalDesiredValue) is not { } desired)
        {
            return Skipped(DeviceDesiredWriteSkipReason.MissingDesiredValue);
        }

        if (switched && projection.DesiredSource is ProfileSource.None)
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

        // An uncertain write may already have happened, so that same write is never repeated
        // automatically; the user choosing it again is what repeats it. A different desired value, such
        // as another game's profile, is a new write and goes ahead. No readback is waited for: a device
        // like the Ally never delivers one, and waiting left every later profile unapplied for the whole
        // session (2026-09-29).
        if (view.LastResult is { Outcome: CommandOutcome.Indeterminate or CommandOutcome.TimedOut }
            && view.LastCommandValue is { } uncertainValue
            && DeviceCoordinator.SameValue(uncertainValue, desired))
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
