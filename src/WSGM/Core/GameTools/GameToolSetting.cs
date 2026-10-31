using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>A setting mapped onto an existing key of the tool's configuration file.</summary>
internal sealed record GameToolSetting
{
    /// <summary>The setting identity.</summary>
    public string Id { get; init; } = "";

    /// <summary>The label shown to the user.</summary>
    public string Label { get; init; } = "";

    /// <summary>A short explanation.</summary>
    public string Description { get; init; } = "";

    /// <summary>The INI section, matched case-insensitively.</summary>
    public string Section { get; init; } = "";

    /// <summary>The INI key inside that section.</summary>
    public string Key { get; init; } = "";

    /// <summary>The default option value.</summary>
    public string Default { get; init; } = "";

    /// <summary>The selectable options.</summary>
    public GameToolSettingOption[] Options { get; init; } = [];

    /// <summary>Notes keyed by lowercase GPU vendor (<c>nvidia</c>, <c>amd</c>, <c>intel</c>).</summary>
    public Dictionary<string, string> VendorNotes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
