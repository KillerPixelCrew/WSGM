namespace WSGM.Core;

/// <summary>What to do with the variable-refresh state when the running application changes.</summary>
internal enum PerAppVrrAction
{
    /// <summary>Write a concrete on/off state.</summary>
    Apply,

    /// <summary>Change nothing. WSGM never set a state, so it has none to take back.</summary>
    Leave
}
