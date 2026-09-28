using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The windows a theme's tab names stand for, in CSS Loader's vocabulary.</summary>
/// <remarks>
///     Mirrors <c>DEFAULT_MAPPINGS</c> and <c>extend_tabs</c> in <c>css_inject.py</c> (b1bc683). A
///     name is looked up in the theme's own aliases first, then here, and anything unknown is a
///     target as written: a whole-title regular expression, <c>~text~</c> for a URL substring, or
///     <c>!name</c> for a class on the document's root elements. The expansion is what the toolkit's
///     theme-styles gate matches windows against.
/// </remarks>
public static class ThemeTargets
{
    /// <summary>The Big Picture window's own name on the Windows client, as a whole-name pattern.</summary>
    public const string BigPictureWindowName = @"SP( BPM_uid\d+)?";

    /// <summary>CSS Loader's own aliases, including the legacy names older themes still use.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultMappings { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["desktop"] = ["Steam|SteamLibraryWindow"],
            ["desktopchat"] = ["!friendsui-container"],
            ["desktoppopup"] =
            [
                "OverlayBrowser_Browser", "SP Overlay:.*", "notificationtoasts_.*", "SteamBrowser_Find",
                "OverlayTab\\d+_Find", "!ModalDialogPopup", "!FullModalOverlay"
            ],
            ["desktopoverlay"] = ["desktoppopup"],
            ["desktopcontextmenu"] = [".*Menu", ".*Supernav"],
            // The Windows client names its Big Picture window "SP BPM_uid<n>", titles it in the
            // local language and puts none of CSSLoader's markers in its URL (measured
            // 2026-09-28), so Big Picture is also named by that window name. The toolkit's gate
            // tries a title pattern against the window's name as well as its title.
            ["bigpicture"] =
                ["~Valve Steam Gamepad/default~", "~Valve%20Steam%20Gamepad~", BigPictureWindowName],
            ["bigpictureoverlay"] = ["QuickAccess", "MainMenu"],
            ["store"] = ["~https://store.steampowered.com~", "~https://steamcommunity.com~"],
            ["SP"] = ["bigpicture"],
            ["Steam Big Picture Mode"] = ["bigpicture"],
            ["MainMenu"] = ["MainMenu.*"],
            ["MainMenu_.*"] = ["MainMenu"],
            ["QuickAccess"] = ["QuickAccess.*"],
            ["QuickAccess_.*"] = ["QuickAccess"],
            ["Steam"] = ["desktop"],
            ["SteamLibraryWindow"] = ["desktop"],
            ["All"] = ["bigpicture", "bigpictureoverlay"]
        };

    /// <summary>Expands tab names to the targets they stand for.</summary>
    /// <param name="tabs">The names as the manifest wrote them.</param>
    /// <param name="themeMappings">The theme's own aliases.</param>
    /// <returns>The targets, each once, in the order the names first expand to them.</returns>
    /// <remarks>
    ///     An empty list expands to the theme's <c>default</c> alias, or to nothing. Each alias is
    ///     expanded once per call, so an alias that names itself, or a wide alias named again and
    ///     again, costs no more than the manifest's length: a theme cannot hang the loader.
    /// </remarks>
    public static IReadOnlyList<string> Expand(
        IReadOnlyList<string> tabs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> themeMappings)
    {
        List<string> expanded = [];
        Expand(tabs, themeMappings, expanded, new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        return expanded;
    }

    private static void Expand(
        IReadOnlyList<string> tabs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> themeMappings,
        List<string> into,
        HashSet<string> listed,
        HashSet<string> expanded)
    {
        if (tabs.Count == 0)
        {
            if (themeMappings.TryGetValue("default", out var fallback) && expanded.Add("\0default"))
            {
                Expand(fallback, themeMappings, into, listed, expanded);
            }

            return;
        }

        foreach (var tab in tabs)
        {
            if (themeMappings.TryGetValue(tab, out var aliases) || DefaultMappings.TryGetValue(tab, out aliases))
            {
                if (expanded.Add(tab))
                {
                    Expand(aliases, themeMappings, into, listed, expanded);
                }
            }
            else if (listed.Add(tab))
            {
                into.Add(tab);
            }
        }
    }
}
