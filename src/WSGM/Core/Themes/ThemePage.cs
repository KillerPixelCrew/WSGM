using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One page of listings and how many there are in all.</summary>
/// <param name="Total">How many themes match, across every page.</param>
/// <param name="Items">This page's listings.</param>
public sealed record ThemePage(int Total, IReadOnlyList<ThemeStoreSummary> Items);
