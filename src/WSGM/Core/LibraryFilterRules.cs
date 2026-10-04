using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

using static WSGM.Core.AppConfigDefaults;

namespace WSGM.Core;

internal static class LibraryFilterRules
{
    internal static IReadOnlyList<string> Normalize(FilterNode node)
    {
        ConfigRepair.NormalizeEnums(node);
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
