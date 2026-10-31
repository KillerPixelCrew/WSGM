namespace WSGM.Shell;

/// <summary>One queued device power transition.</summary>
/// <param name="suspend">Whether the transition quiesces the device cycle.</param>
/// <param name="systemSleep">Whether a system suspend or resume asked for it, not a lock.</param>
/// <param name="reason">The notification that asked for it, for the log.</param>
internal sealed class DevicePowerTransition(bool suspend, bool systemSleep, string reason)
{
    /// <summary>Set under the queue's gate when an opposite edge or shutdown dropped it before it ran.</summary>
    internal bool Cancelled;

    /// <summary>Set under the queue's gate once it began running.</summary>
    internal bool Started;

    /// <summary>Whether the transition quiesces the device cycle.</summary>
    internal bool Suspend { get; } = suspend;

    /// <summary>Whether a system suspend or resume asked for it, not a lock.</summary>
    internal bool SystemSleep { get; } = systemSleep;

    /// <summary>The notification that asked for it, for the log.</summary>
    internal string Reason { get; } = reason;
}
