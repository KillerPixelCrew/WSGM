namespace WSGM.Core;

/// <summary>Next action while the captured Explorer retires its desktop shell.</summary>
internal enum ExplorerExitAction
{
    /// <summary>Continue observing ownership and absence without dispatching another request.</summary>
    Wait,

    /// <summary>The captured shell process has exited and its desktop has remained absent long enough.</summary>
    Complete,

    /// <summary>The requested transition authorizes terminating the captured shell after its grace.</summary>
    ForceStop
}
