using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Picks every slot anew from the library, as Animation Changer's shuffle does.</summary>
public static class AnimationShuffle
{
    /// <summary>A new assignment: for each slot, one animation that fits it, chosen at random.</summary>
    /// <param name="entries">The library.</param>
    /// <param name="exclusions">Library ids never picked.</param>
    /// <param name="random">The source of choice.</param>
    /// <returns>The library id per slot, empty where nothing fits.</returns>
    public static IReadOnlyDictionary<string, string> Pick(
        IReadOnlyList<AnimationEntry> entries, IReadOnlyCollection<string> exclusions, Random random)
    {
        Dictionary<string, string> picked = new(StringComparer.Ordinal);
        foreach (var slot in AnimationSlots.All)
        {
            var pool = entries
                .Where(entry => AnimationTargets.Fits(entry.Target, slot) && !exclusions.Contains(entry.Id))
                .ToList();
            picked[slot] = pool.Count == 0 ? string.Empty : pool[random.Next(pool.Count)].Id;
        }

        return picked;
    }
}
