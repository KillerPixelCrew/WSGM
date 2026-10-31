using System;

namespace WSGM.Core;

/// <summary>The plugin-published bounds of the primary power capability.</summary>
/// <param name="Minimum">Lowest power limit in watts.</param>
/// <param name="Maximum">Highest power limit in watts.</param>
/// <param name="Step">Positive device increment in watts.</param>
internal sealed record AutoTdpLimits(int Minimum, int Maximum, int Step)
{
    /// <summary>Whether the bounds describe a usable control.</summary>
    internal bool IsUsable => Step > 0 && Minimum > 0 && Maximum >= Minimum + Step;

    /// <summary>Clamps a candidate to the inclusive wattage bounds without rounding it to the step grid.</summary>
    /// <param name="value">Candidate limit in watts; the caller maintains step alignment.</param>
    /// <returns>The candidate bounded by Minimum and Maximum.</returns>
    /// <exception cref="ArgumentException">Minimum exceeds Maximum.</exception>
    internal int Clamp(int value)
    {
        return Math.Clamp(value, Minimum, Maximum);
    }
}
