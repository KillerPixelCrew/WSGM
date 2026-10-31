namespace WSGM.Core;

/// <summary>Normalizes the stored animations configuration in place before consumers use it.</summary>
internal static class AnimationRules
{
    /// <summary>Repairs the animations section: a trimmed boot id and whole set-aside strings.</summary>
    /// <param name="animations">The section.</param>
    internal static void Normalize(AnimationsConfig animations)
    {
        animations.Boot = animations.Boot?.Trim() ?? string.Empty;
        if (animations.SteamSetAside is { } setAside)
        {
            setAside.MovieId ??= string.Empty;
            setAside.LocalPath ??= string.Empty;
        }
    }
}
