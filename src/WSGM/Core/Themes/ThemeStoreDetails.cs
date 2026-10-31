using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Everything the store says about one theme.</summary>
/// <param name="Summary">The listing.</param>
/// <param name="Description">The author's description, or empty.</param>
/// <param name="Dependencies">The themes it needs.</param>
/// <param name="Source">Where its source lives, or null.</param>
public sealed record ThemeStoreDetails(
    ThemeStoreSummary Summary,
    string Description,
    IReadOnlyList<ThemeStoreDependency> Dependencies,
    string? Source);
