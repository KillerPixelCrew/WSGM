using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What both processor boost rows show for the current profile layers.</summary>
/// <param name="Effective">The layer's preference when one is set, else what Windows reports on AC, or null.</param>
/// <param name="Source">Which profile layer the preference came from.</param>
/// <param name="SourcesMatch">Whether Windows holds the same mode plugged in and on battery.</param>
internal sealed record CpuBoostView(CpuBoostMode? Effective, ProfileSource Source, bool SourcesMatch)
{
    /// <summary>The sentence both rows add about plugged-in and battery modes.</summary>
    internal string SourcesText => SourcesMatch
        ? "Applies to both plugged in and battery."
        : "Plugged in and battery currently differ; choosing sets both.";
}
