using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The targets the store can filter by, each with its count, and the orders it offers.</summary>
/// <param name="Filters">Each target and how many themes carry it.</param>
/// <param name="Orders">The order names, as the store spells them.</param>
public sealed record ThemeStoreFilters(IReadOnlyDictionary<string, int> Filters, IReadOnlyList<string> Orders);
