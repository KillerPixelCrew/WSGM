using System;
using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Picks the boot movie anew from the library, as Animation Changer's shuffle does.</summary>
public static class AnimationShuffle
{
    /// <summary>One library id chosen at random, or empty when the library is empty.</summary>
    /// <param name="entries">The library.</param>
    /// <param name="random">The source of choice.</param>
    /// <returns>The id, or empty.</returns>
    public static string Pick(IReadOnlyList<AnimationEntry> entries, Random random)
    {
        return entries.Count == 0 ? string.Empty : entries[random.Next(entries.Count)].Id;
    }
}
