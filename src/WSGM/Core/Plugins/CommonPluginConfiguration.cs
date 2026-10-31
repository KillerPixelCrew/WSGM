using System;
using System.Collections.Generic;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>Saved user preferences for one common plugin instance.</summary>
public sealed class CommonPluginConfiguration
{
    /// <summary>Plugin package identity.</summary>
    public string PluginId { get; set; } = "";

    /// <summary>Host-selected instance identity.</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>Increasing user-intent revision.</summary>
    public long Revision { get; set; }

    /// <summary>
    ///     Explicitly saved values only; readback and declaration defaults never populate this map. Null
    ///     only in a hand-edited file, which is preserved rather than overwritten.
    /// </summary>
    public Dictionary<string, PluginValue>? Values { get; set; } = new(StringComparer.Ordinal);
}
