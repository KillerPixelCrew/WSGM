using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>The frame caps worth offering on a display. A cap is a free number and moves no display state.</summary>
public static class FrameLimitCaps
{
    /// <summary>Lowest cap the slider offers: below 30 FPS is not worth playing at.</summary>
    private const int Floor = 30;

    /// <summary>The lowest and highest frame cap a display can be asked for.</summary>
    /// <param name="refreshRates">The refresh rates Windows lists for the current mode.</param>
    /// <returns>The inclusive range, or null when no rate is high enough to carry a cap.</returns>
    public static (int Minimum, int Maximum)? Range(IReadOnlyList<int> refreshRates)
    {
        var ceiling = refreshRates.Count is 0 ? 0 : refreshRates.Max();
        return ceiling < Floor ? null : (Floor, ceiling);
    }

    /// <summary>The frame caps worth offering.</summary>
    /// <param name="refreshRates">The refresh rates Windows lists for the current mode.</param>
    /// <returns>Zero first for off, then every integer in <see cref="Range" />; only zero without a range.</returns>
    public static IReadOnlyList<int> Options(IReadOnlyList<int> refreshRates)
    {
        if (Range(refreshRates) is not { } range)
        {
            return [0];
        }

        List<int> caps = [0];
        for (var cap = range.Minimum; cap <= range.Maximum; cap++)
        {
            caps.Add(cap);
        }

        return caps;
    }
}
