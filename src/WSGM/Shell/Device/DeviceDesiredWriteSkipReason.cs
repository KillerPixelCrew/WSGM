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
