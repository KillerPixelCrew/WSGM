using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The three animations Steam plays, and the files the Windows client asks for.</summary>
/// <remarks>
///     <para>
///         Steam's client serves <c>/uioverrides/&lt;path&gt;</c> from <c>config\uioverrides</c> and,
///         for a handful of resources, HEAD-requests the override before falling back to its own
///         copy (<c>steamui</c> bundle, the overrideable-resource hook; live on 2026-09-28). Which
///         path it asks for is decided in the bundle, not by the device: the startup movie is
///         <c>/movies/bigpicture_startup.webm</c> everywhere but SteamOS, and the suspend movies are
///         looked up under the SteamOS names <c>steam_os_suspend.webm</c> and
///         <c>steam_os_suspend_from_throbber.webm</c> on every device, the Deck and OLED variants
///         being only the stock fallback. So those three are the override names on Windows.
///     </para>
///     <para>
///         The lookup is cached for the life of the document, so an override written while Steam
///         runs shows after the next Steam start. Steam's own Startup Movie setting must be the
///         default for the boot override to be asked for at all.
///     </para>
/// </remarks>
public static class AnimationSlots
{
    /// <summary>The movie Big Picture starts with.</summary>
    public const string Boot = "boot";

    /// <summary>The movie played while suspending from the shell.</summary>
    public const string Suspend = "suspend";

    /// <summary>The movie played while suspending from inside a game.</summary>
    public const string Throbber = "throbber";

    /// <summary>Every slot, in the order the pages list them.</summary>
    public static IReadOnlyList<string> All { get; } = [Boot, Suspend, Throbber];

    /// <summary>Whether a string names a slot.</summary>
    /// <param name="slot">The candidate.</param>
    public static bool IsSlot(string slot)
    {
        return slot is Boot or Suspend or Throbber;
    }

    /// <summary>The file name the Windows client asks the override route for.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The name under <c>config\uioverrides\movies</c>.</returns>
    public static string FileName(string slot)
    {
        return slot switch
        {
            Boot => "bigpicture_startup.webm",
            Suspend => "steam_os_suspend.webm",
            Throbber => "steam_os_suspend_from_throbber.webm",
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Not a slot.")
        };
    }

    /// <summary>The kind of animation a slot takes, as the repository categorizes them.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns><see cref="AnimationTargets.Boot" /> or <see cref="AnimationTargets.Suspend" />.</returns>
    public static string Target(string slot)
    {
        return slot == Boot ? AnimationTargets.Boot : AnimationTargets.Suspend;
    }

    /// <summary>The slot's name as the pages show it.</summary>
    /// <param name="slot">The slot.</param>
    public static string Label(string slot)
    {
        return slot switch
        {
            Boot => "Boot",
            Suspend => "Suspend",
            Throbber => "Suspend from a game",
            _ => slot
        };
    }
}

/// <summary>What an animation is for, as SteamDeckRepo tags it.</summary>
public static class AnimationTargets
{
    /// <summary>A startup movie.</summary>
    public const string Boot = "boot";

    /// <summary>A suspend movie, for either suspend slot.</summary>
    public const string Suspend = "suspend";

    /// <summary>A file the user brought, offered for every slot.</summary>
    public const string Any = "any";

    /// <summary>Whether an animation with a target fits a slot.</summary>
    /// <param name="target">The animation's target.</param>
    /// <param name="slot">The slot.</param>
    public static bool Fits(string target, string slot)
    {
        return target == Any || target == AnimationSlots.Target(slot);
    }
}
