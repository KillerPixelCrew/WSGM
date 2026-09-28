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
            ["bigpicture"] = ["~Valve Steam Gamepad/default~", "~Valve%20Steam%20Gamepad~"],
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
    /// <returns>The targets, in the order the names expand to.</returns>
    /// <remarks>
    ///     An empty list expands to the theme's <c>default</c> alias, or to nothing; a name that
    ///     refers to itself is expanded until the bound is reached, so a theme cannot hang the loader.
    /// </remarks>
    public static IReadOnlyList<string> Expand(
        IReadOnlyList<string> tabs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> themeMappings)
    {
        List<string> expanded = [];
        Expand(tabs, themeMappings, expanded, 0);
        return expanded;
    }

    private static void Expand(
        IReadOnlyList<string> tabs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> themeMappings,
        List<string> into,
        int depth)
    {
        if (depth > 16)
        {
            return;
        }

        if (tabs.Count == 0)
        {
            if (themeMappings.TryGetValue("default", out var fallback))
            {
                Expand(fallback, themeMappings, into, depth + 1);
            }

            return;
        }

        foreach (var tab in tabs)
        {
            if (themeMappings.TryGetValue(tab, out var own))
            {
                Expand(own, themeMappings, into, depth + 1);
            }
            else if (DefaultMappings.TryGetValue(tab, out var known))
            {
                Expand(known, themeMappings, into, depth + 1);
            }
            else
            {
                into.Add(tab);
            }
        }
    }
}
