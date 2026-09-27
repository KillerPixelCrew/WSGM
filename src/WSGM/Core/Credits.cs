using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One person or project WSGM thanks, as the About pages show it.</summary>
/// <param name="Name">Who or what.</param>
/// <param name="Role">What they did for WSGM, one sentence.</param>
/// <param name="Url">Where to find them.</param>
public sealed record Credit(string Name, string Role, string Url);

/// <summary>
///     What the About page in Settings and the About page in the overlay show: the version, the
///     project links and the people and projects WSGM thanks. One list, so the two surfaces and the
///     README cannot drift apart on who is credited.
/// </summary>
public static class Credits
{
    /// <summary>The project's home.</summary>
    public const string ProjectUrl = "https://github.com/KillerPixelCrew/WSGM";

    /// <summary>Where a problem is reported.</summary>
    public const string IssuesUrl = "https://github.com/KillerPixelCrew/WSGM/issues";

    /// <summary>The licence text as published.</summary>
    public const string LicenseUrl = "https://github.com/KillerPixelCrew/WSGM/blob/master/LICENSE";

    /// <summary>The copyright line.</summary>
    public const string Copyright = "Copyright (C) 2026 NightHammer1000";

    /// <summary>The licence in one line.</summary>
    public const string License = "GNU General Public License, version 3 or later";

    /// <summary>The people WSGM thanks, in the order they are shown.</summary>
    public static IReadOnlyList<Credit> People { get; } =
    [
        new("Brochacho", "Main tester of every release, and donor of the ROG Xbox Ally X in the Device Lab.",
            "https://github.com/BrochachoTheBro")
    ];

    /// <summary>The projects WSGM's features build on, in the order they are shown.</summary>
    public static IReadOnlyList<Credit> Projects { get; } =
    [
        new("TabMaster", "Filter tabs and tab-strip control, reimplemented for Windows.",
            "https://github.com/Tormak9970/TabMaster"),
        new("MicroSDeck", "Per-card libraries, reimplemented for Windows.",
            "https://github.com/CEbbinghaus/MicroSDeck"),
        new("decky-steamgriddb", "The artwork flow, reimplemented for Windows.",
            "https://github.com/SteamGridDB/decky-steamgriddb"),
        new("SpecialK", "Its ValvePlug informed the Steam Input Lease's blocking model.",
            "https://github.com/SpecialKO/SpecialK"),
        new("Handheld Companion", "The reference for device behaviour on the ROG Ally family.",
            "https://github.com/Valkirie/HandheldCompanion")
    ];

    /// <summary>The running version, such as <c>2.0.1</c>, with the build revision after it.</summary>
    public static string VersionText { get; } = Describe(typeof(Credits).Assembly.GetName().Version);

    internal static string Describe(Version? version)
    {
        if (version is null)
        {
            return "development build";
        }

        var release = $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        return version.Revision > 0 ? $"{release} (build {version.Revision})" : release;
    }
}
