using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Required payload contents and copy exclusions.</summary>
internal sealed record GameToolPayload
{
    /// <summary>Files required at the payload root.</summary>
    public string[] Required { get; init; } = [];

    /// <summary>Directories additionally required by a version family.</summary>
    public Dictionary<string, string[]> FamilyDirectories { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Upstream files never copied into a game, matched with DOS wildcards.</summary>
    public string[] Exclude { get; init; } = [];

    /// <summary>Files an exclusion can never remove.</summary>
    public string[] NeverExclude { get; init; } = [];
}
