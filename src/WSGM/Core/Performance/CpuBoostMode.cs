namespace WSGM.Core;

/// <summary>Windows' processor performance boost mode, with HC's five choices.</summary>
/// <remarks>
///     The values are the ones Windows stores for <c>PERFBOOSTMODE</c> and the ones Handheld
///     Companion writes for its "CPU boost mode" (<c>CPUBoostLevel</c>). Windows also defines
///     aggressive-at-guaranteed modes 5 and 6; HC does not offer them, so neither does this.
/// </remarks>
public enum CpuBoostMode
{
    /// <summary>No boost: cores stay at their base frequency.</summary>
    Disabled = 0,

    /// <summary>Windows' default: boost when there is headroom.</summary>
    Enabled = 1,

    /// <summary>Boost as hard as the platform allows.</summary>
    Aggressive = 2,

    /// <summary>Enabled, but weighed against energy efficiency.</summary>
    EfficientEnabled = 3,

    /// <summary>Aggressive, but weighed against energy efficiency.</summary>
    EfficientAggressive = 4
}
