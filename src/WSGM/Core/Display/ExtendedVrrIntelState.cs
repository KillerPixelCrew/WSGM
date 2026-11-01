namespace WSGM.Core;

/// <summary>
///     The Intel variable refresh state saved before the extended VRR range first changed it, and restored when the
///     range is turned off or WSGM is uninstalled.
/// </summary>
/// <remarks>
///     Saved once, like the Atlas8Tweaks VRR Fix's <c>original-state.json</c>: <c>MEASURED</c> when nothing of the
///     extended range was in force, otherwise the panel's measured <c>FACTORY_DEFAULT</c>. Cleared once the state is
///     back. A present record is also the evidence that WSGM, not another tool, turned the range on.
/// </remarks>
public sealed record ExtendedVrrIntelState
{
    /// <summary><c>MEASURED</c> or <c>FACTORY_DEFAULT</c>.</summary>
    public string Source { get; init; } = "";

    /// <summary>The Intel Arc Sync profile number.</summary>
    public int ProfileId { get; init; }

    /// <summary>The profile's minimum refresh rate.</summary>
    public double MinimumHz { get; init; }

    /// <summary>The profile's maximum refresh rate.</summary>
    public double MaximumHz { get; init; }

    /// <summary>The profile's frame time increase limit, in microseconds.</summary>
    public uint MaxIncreaseUs { get; init; }

    /// <summary>The profile's frame time decrease limit, in microseconds.</summary>
    public uint MaxDecreaseUs { get; init; }

    /// <summary>Whether Intel's low frame rate solution (LFC) was on.</summary>
    public bool LowFpsSolutionEnabled { get; init; }

    /// <summary>Whether Intel's high frame rate solution was on.</summary>
    public bool HighFpsSolutionEnabled { get; init; }
}
