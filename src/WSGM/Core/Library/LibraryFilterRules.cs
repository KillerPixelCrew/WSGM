using System.Linq;

namespace WSGM.Core;

/// <summary>Repairs nullable members in saved library filter trees without changing filter intent.</summary>
internal static class LibraryFilterRules
{
    /// <summary>Recursively replaces null fields and removes null child entries.</summary>
    /// <param name="node">Non-null root of an acyclic mutable filter tree.</param>
    /// <remarks>This structural repair does not validate predicate meaning.</remarks>
    internal static void Normalize(FilterNode node)
    {
        node.CollectionId ??= "";
        node.Pattern ??= "";
        node.ContentId ??= "";
        node.Children = [.. (node.Children ?? []).Where(static child => child is not null)];
        node.TagIds ??= [];
        node.AppIds ??= [];
        foreach (var child in node.Children)
        {
            Normalize(child);
        }
    }
}
