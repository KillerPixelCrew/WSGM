namespace WSGM.Core;

/// <summary>How WSGM offers to place threads across a hybrid CPU's core types.</summary>
/// <remarks>
///     These are WSGM's names for values Windows publishes for its thread scheduling policy. Windows
///     names them itself, which is why they are the ones WSGM writes: its heterogeneous-policy setting
///     is enumerated only as "use heterogeneous policy 0..4" with no published meaning, so WSGM reads
///     and restores that value but never chooses one.
/// </remarks>
internal enum HybridCoreMode
{
    /// <summary>Windows places threads. The state a machine ships in.</summary>
    Automatic,

    /// <summary>Both core types stay eligible; performance cores are preferred.</summary>
    PreferPerformance,

    /// <summary>Both core types stay eligible; efficiency cores are preferred.</summary>
    PreferEfficiency,

    /// <summary>Only performance cores are eligible.</summary>
    PerformanceOnly,

    /// <summary>Only efficiency cores are eligible.</summary>
    EfficiencyOnly
}
