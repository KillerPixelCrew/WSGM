using System.Collections.Generic;

namespace WSGM.Shell;

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
