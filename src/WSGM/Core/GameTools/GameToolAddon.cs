using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.Core;

/// <summary>
///     An optional single-file plugin a tool loads, such as OptiPatcher for OptiScaler, offered only for the games
///     its publisher lists as supported.
/// </summary>
/// <remarks>
///     The add-on's file is installed through the tool's own transaction as one more owned file, so journaling,
///     rollback, revalidation, repair and remove cover it without a second install path.
/// </remarks>
internal sealed record GameToolAddon
{
    /// <summary>The stable identity, recorded in the manifest.</summary>
    public string Id { get; init; } = "";

    /// <summary>The product name.</summary>
    public string Name { get; init; } = "";

    /// <summary>What it does for a supported game.</summary>
    public string Description { get; init; } = "";

    /// <summary>The upstream project page.</summary>
    public string Homepage { get; init; } = "";

    /// <summary>The upstream license identifier.</summary>
    public string License { get; init; } = "";

    /// <summary>Where its builds are published; the asset is the file itself, not an archive.</summary>
    public GameToolSource Source { get; init; } = new();

    /// <summary>Where the file goes, relative to the tool's target directory.</summary>
    public string File { get; init; } = "";

    /// <summary>Another location for a version family, such as the 10.x <c>OptiScaler/plugins</c> folder.</summary>
    public Dictionary<string, string> FamilyFiles { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The INI section of the key that makes the tool load the add-on.</summary>
    public string ConfigSection { get; init; } = "";

    /// <summary>The INI key that makes the tool load the add-on.</summary>
    public string ConfigKey { get; init; } = "";

    /// <summary>The raw INI value written while the add-on is installed.</summary>
    public string ConfigOn { get; init; } = "";

    /// <summary>The raw INI value written once the add-on is removed.</summary>
    public string ConfigOff { get; init; } = "";

    /// <summary>Lowercase GPU vendors the add-on helps; it is suggested only when one of them is present.</summary>
    public string[] Vendors { get; init; } = [];

    /// <summary>Notes keyed by lowercase GPU vendor.</summary>
    public Dictionary<string, string> VendorNotes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The HTTPS source file that lists the supported games.</summary>
    public string SupportUrl { get; init; } = "";

    /// <summary>How executable names are read from <see cref="SupportUrl" />.</summary>
    public GameToolAddonSupportRule[] SupportRules { get; init; } = [];

    /// <summary>The file name the add-on is installed under.</summary>
    internal string FileName => Path.GetFileName(File);

    /// <summary>Where the file goes for a version family.</summary>
    /// <param name="family">The installed or packaged family.</param>
    /// <returns>A forward-slash path relative to the target directory.</returns>
    internal string FileFor(string family)
    {
        return FamilyFiles.GetValueOrDefault(family) ?? File;
    }
}
