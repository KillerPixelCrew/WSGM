using System.Collections.Generic;

namespace WSGM.Core;

internal static class AnimationRules
{
    /// <summary>Repairs the animations section: a trimmed boot id and whole set-aside strings.</summary>
    /// <param name="animations">The section.</param>
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
