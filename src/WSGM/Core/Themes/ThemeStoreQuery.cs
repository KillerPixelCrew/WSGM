namespace WSGM.Core;

/// <summary>One page of the store, as asked for.</summary>
/// <param name="Page">The page, from one.</param>
/// <param name="PerPage">How many themes a page holds.</param>
/// <param name="Filter">The target to show, or <c>All</c>.</param>
/// <param name="Order">The order the store lists in, one of its own names.</param>
/// <param name="Search">The search text, or empty.</param>
public sealed record ThemeStoreQuery(int Page, int PerPage, string Filter, string Order, string Search)
{
    /// <summary>The filter that shows every target.</summary>
    public const string AllFilter = "All";

    /// <summary>The order CSS Loader opens on.</summary>
    public const string DefaultOrder = "Last Updated";

    /// <summary>The first page of everything, newest first.</summary>
    public static ThemeStoreQuery Default { get; } = new(1, 50, AllFilter, DefaultOrder, string.Empty);
}
