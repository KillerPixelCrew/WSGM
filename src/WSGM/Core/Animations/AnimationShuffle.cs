using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Picks the boot movie anew from the library, as Animation Changer's shuffle does.</summary>
public static class AnimationShuffle
{
    /// <summary>One library id chosen at random, or empty when nothing is left to choose from.</summary>
    /// <param name="entries">The library.</param>
    /// <param name="exclusions">Library ids never picked.</param>
    /// <param name="random">The source of choice.</param>
    /// <returns>The id, or empty.</returns>
    public static string Pick(
        IReadOnlyList<AnimationEntry> entries, IReadOnlyCollection<string> exclusions, Random random)
    {
        var pool = entries.Where(entry => !exclusions.Contains(entry.Id)).ToList();
        return pool.Count == 0 ? string.Empty : pool[random.Next(pool.Count)].Id;
    }
}
