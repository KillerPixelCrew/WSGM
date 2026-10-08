using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Why profile reconciliation declined an automatic capability write.</summary>
internal enum DeviceDesiredWriteSkipReason
{
    /// <summary>The descriptor does not support writes.</summary>
    Unsupported,

    /// <summary>No value is selected in the applicable profile layers.</summary>
    MissingDesiredValue,

    /// <summary>The switched value has no admitted profile source.</summary>
    MissingDesiredSource,

    /// <summary>The current capability state is unavailable.</summary>
    Unavailable,

    /// <summary>The stored value no longer fits current descriptor bounds.</summary>
    DesiredValueOutOfRange,

    /// <summary>The observation is stale or faulted; unknown readback alone is allowed.</summary>
    UntrustedState,

    /// <summary>The observed value already equals the desired value.</summary>
    AlreadyApplied,

    /// <summary>Another value is pending for this capability.</summary>
    CommandPending,

    /// <summary>The same value was previously timed out or indeterminate and needs an explicit user action.</summary>
    PreviousResultUncertain
}

/// <summary>The shared admission decision for automatically restoring a desired device value.</summary>
/// <param name="DesiredValue">Value admitted for this automatic write; null when skipped.</param>
/// <param name="SkipReason">Reason the write was skipped, or null when admitted.</param>
internal readonly record struct DeviceDesiredWriteAdmission(
    CapabilityValue? DesiredValue,
    DeviceDesiredWriteSkipReason? SkipReason)
{
    /// <summary>Whether automatic reconciliation may submit the selected desired value.</summary>
    internal bool Admitted => SkipReason is null;

    /// <summary>Admits a desired profile write against current availability, value and previous outcome.</summary>
    /// <param name="view">Current router snapshot for one capability instance.</param>
    /// <returns>The selected desired value, or the first applicable skip reason; performs no I/O.</returns>
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

        if (projection.State.ObservedValue is { } observed && CapabilityValues.Same(observed, desired))
        {
            return Skipped(DeviceDesiredWriteSkipReason.AlreadyApplied);
        }

        if (projection.PendingValue is not null)
        {
            return Skipped(DeviceDesiredWriteSkipReason.CommandPending);
        }

        // Block repeating the same uncertain value automatically; a different desired value is a new write.
        if (view.LastResult is { Outcome: CommandOutcome.Indeterminate or CommandOutcome.TimedOut }
            && view.LastCommandValue is { } uncertainValue
            && CapabilityValues.Same(uncertainValue, desired))
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
