using System.Collections.Generic;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>A bounded shared-performance snapshot for one overlay projection.</summary>
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
    Progress,
}

/// <summary>Immutable, presentation-only content for a descriptor-driven overlay row.</summary>
internal sealed record DescriptorRow(
    string Id,
    string Title,
    string Description,
    string TrailingText,
    bool CanInvoke,
    DescriptorStatus Status = DescriptorStatus.None)
{
    /// <summary>The range this row is set over, or null when pressing it is the interaction.</summary>
    /// <remarks>
    /// A row that carries a range or options is a control, not a button. Cycling was fine while a
    /// row had four sensible values; a frame limit has hundreds, and stepping to 280 one preset at
    /// a time is not an interaction anyone completes.
    /// </remarks>
    public DescriptorRange? Range { get; init; }

    /// <summary>The named values this row chooses between, empty when it is not a choice.</summary>
    public IReadOnlyList<DescriptorOption> Options { get; init; } = [];

    /// <summary>The value in force, for a row with a range or options.</summary>
    public int? Value { get; init; }
}

/// <summary>The bounds of a row the user sets with a slider.</summary>
/// <param name="Minimum">Inclusive lower bound.</param>
/// <param name="Maximum">Inclusive upper bound.</param>
/// <param name="Step">Movement per pad nudge; at least 1.</param>
/// <param name="OffBelow">
/// The lowest value the row means anything at, or zero when every position is a real value. The
/// frame limit has one: the slider must still reach zero, because zero is how the row is switched
/// off and there is no separate switch, but the caps under the panel's floor are not values any
/// other surface will accept. Everything below this reads and commits as zero rather than as a cap
/// the Quick Access row would then refuse to draw.
/// </param>
internal readonly record struct DescriptorRange(int Minimum, int Maximum, int Step, int OffBelow = 0);

/// <summary>One named value of a row the user picks from a dropdown.</summary>
/// <param name="Value">The value written when it is chosen.</param>
/// <param name="Label">What the user reads.</param>
internal sealed record DescriptorOption(int Value, string Label);

/// <summary>
/// Renders a closed semantic row descriptor with the shared card appearance and status vocabulary.
/// </summary>
internal sealed class DescriptorStatusRow : CardButton
{
    internal void Apply(DescriptorRow descriptor)
    {
        Tag = descriptor.Id;
        Title = descriptor.Title;
        Description = descriptor.Description;
        TrailingText = descriptor.TrailingText;
        IsEnabled = descriptor.CanInvoke;
        IconGeometry = Icons.Gear;
        StatusBrush = StatusBrushFor(descriptor.Status);
        AutomationProperties.SetName(this, descriptor.Title);
        AutomationProperties.SetHelpText(this, descriptor.Description);
    }

    private IBrush? StatusBrushFor(DescriptorStatus status)
    {
        string? resource = status switch
        {
            DescriptorStatus.Available => "HcSuccessBrush",
            DescriptorStatus.Warning or DescriptorStatus.Stale => "HcWarningBrush",
            DescriptorStatus.Faulted => "HcDangerBrush",
            DescriptorStatus.ExternallyOwned or DescriptorStatus.Unsupported => "HcTextMutedBrush",
            DescriptorStatus.Progress => "HcWarningBrush",
            _ => null,
        };
        return resource is null ? null : this.FindResource(resource) as IBrush;
    }
}
