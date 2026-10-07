using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Normalizes the stored animations configuration in place before consumers use it.</summary>
internal static class AnimationRules
{
    /// <summary>Repairs the animations section: a trimmed boot id and whole set-aside strings.</summary>
    /// <param name="animations">The section.</param>
    /// <returns>An empty diagnostic list; these repairs do not produce warning entries.</returns>
    internal static IReadOnlyList<string> Normalize(AnimationsConfig animations)
    {
        animations.Boot = animations.Boot?.Trim() ?? string.Empty;
        if (animations.SteamSetAside is { } setAside)
        {
            setAside.MovieId ??= string.Empty;
            setAside.LocalPath ??= string.Empty;
        }

        return [];
    }
}
