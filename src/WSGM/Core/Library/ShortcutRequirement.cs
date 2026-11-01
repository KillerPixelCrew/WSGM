using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What one Steam shortcut needs on disk.</summary>
/// <param name="Shortcut">The shortcut as Steam lists it.</param>
/// <param name="ContentId">The managed record its WSGM helper Target names, or empty.</param>
/// <param name="LiteralPaths">
///     Absolute paths taken as written: a follow route's install folder, or the Target and Start In of any other
///     shortcut. Each means whatever volume holds that drive now.
/// </param>
public sealed record ShortcutRequirement(
    ExistingShortcut Shortcut,
    string ContentId,
    IReadOnlyList<ManagedContentPath> LiteralPaths)
{
    /// <summary>Whether a managed content record, bound to its volume, decides this shortcut.</summary>
    public bool Managed => ContentId.Length > 0;
}
