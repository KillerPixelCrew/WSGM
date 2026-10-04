using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

internal static class LibraryFilterRules
{
    internal static IReadOnlyList<string> Normalize(FilterNode node)
    {
        NormalizeNode(node);
        return [];
    }

    private static void NormalizeNode(FilterNode node)
    {
        node.CollectionId ??= "";
        node.Pattern ??= "";
        node.ContentId ??= "";
        node.Children = [.. (node.Children ?? []).Where(static child => child is not null)];
        node.TagIds ??= [];
        node.AppIds ??= [];
        foreach (var child in node.Children)
        {
            NormalizeNode(child);
        }
    }
}
