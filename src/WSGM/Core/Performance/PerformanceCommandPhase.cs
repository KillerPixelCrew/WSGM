namespace WSGM.Core;

/// <summary>Lifecycle of the last semantic performance command.</summary>
internal enum PerformanceCommandPhase
{
    /// <summary>No command has been submitted.</summary>
    Idle,

    /// <summary>The command is awaiting serialized adapter access.</summary>
    Queued,

    /// <summary>The command is validating, persisting, or writing its value.</summary>
    Applying,

    /// <summary>The preference is saved while the application executable is still unknown.</summary>
    Deferred,

    /// <summary>The adapter accepted the write; immediate readback is not required.</summary>
    Applied,

    /// <summary>The command was refused by admission, validation, or the adapter.</summary>
    Rejected,

    /// <summary>Persistence or processing failed, timed out, or was cancelled after admission.</summary>
    Failed,

    /// <summary>Polling detected a value different from the previous observation.</summary>
    ExternalChange
}
