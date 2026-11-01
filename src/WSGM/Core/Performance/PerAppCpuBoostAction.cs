namespace WSGM.Core;

/// <summary>The action to take on the processor boost mode for a transition.</summary>
internal enum PerAppCpuBoostAction
{
    /// <summary>Write the carried mode.</summary>
    Apply,

    /// <summary>Change nothing.</summary>
    Leave
}
