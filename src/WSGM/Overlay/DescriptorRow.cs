using System.Collections.Generic;

namespace WSGM.Overlay;

/// <summary>A bounded shared-performance snapshot for one overlay projection.</summary>
/// <param name="Visible">Whether the performance section is available for presentation.</param>
/// <param name="Status">Service availability or operation detail.</param>
/// <param name="Rows">Value controls shown with Device power and available to pinning.</param>
/// <param name="ProfileRows">Profile-management controls shown in the Profiles workflow.</param>
internal sealed record PerformanceOverlaySnapshot(
    bool Visible,
    string Status,
    IReadOnlyList<DescriptorRow> Rows,
    IReadOnlyList<DescriptorRow> ProfileRows);

/// <summary>Semantic presentation state shared by descriptor-driven overlay rows.</summary>
internal enum DescriptorStatus
{
    None,
    Available,
    Warning,
    Faulted,
    Stale,
    ExternallyOwned,
    Unsupported,
    Progress
}

/// <summary>Immutable, presentation-only content for a descriptor-driven overlay row.</summary>
/// <param name="Id">Stable semantic identity shared by placements and command routing.</param>
/// <param name="Title">Visible control label.</param>
/// <param name="Description">Supporting explanation or availability reason.</param>
/// <param name="TrailingText">Compact value or activation hint for a command row.</param>
/// <param name="CanInvoke">Whether the current projection permits a command or edit.</param>
/// <param name="Status">Presentation status independent of the stored value.</param>
internal sealed record DescriptorRow(
    string Id,
    string Title,
    string Description,
    string TrailingText,
    bool CanInvoke,
    DescriptorStatus Status = DescriptorStatus.None)
{
    /// <summary>The range this row is set over, or null when pressing it is the interaction.</summary>
    /// <remarks>Range and choice rows use editors; the renderer does not cycle values through a command button.</remarks>
    public DescriptorRange? Range { get; init; }

    /// <summary>The named values this row chooses between, empty when it is not a choice.</summary>
    public IReadOnlyList<DescriptorOption> Options { get; init; } = [];

    /// <summary>The value in force, for a row with a range or options.</summary>
    public int? Value { get; init; }

    /// <summary>
    ///     The profile setting id while the running game's own profile supplies this row's value, else
    ///     null. The row marks it and offers Use global.
    /// </summary>
    public string? OverrideId { get; init; }
}

/// <summary>The bounds of a row the user sets with a slider.</summary>
/// <param name="Minimum">Inclusive lower bound.</param>
/// <param name="Maximum">Inclusive upper bound.</param>
/// <param name="Step">Movement per pad nudge; at least 1.</param>
/// <param name="OffBelow">
///     Lowest nonzero accepted value, or zero when every position is valid. Lower slider positions
///     read and commit as zero (off), preserving the same value domain as the Steam projection.
/// </param>
internal readonly record struct DescriptorRange(int Minimum, int Maximum, int Step, int OffBelow = 0);

/// <summary>One named value of a row the user picks from a dropdown.</summary>
/// <param name="Value">The value written when it is chosen.</param>
/// <param name="Label">What the user reads.</param>
internal sealed record DescriptorOption(int Value, string Label);
