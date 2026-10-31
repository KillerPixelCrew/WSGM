using System;
using System.Collections.Generic;

namespace WSGM.Overlay;

/// <summary>Stores bounded destination-local focus state across overlay window recreation.</summary>
internal sealed class OverlayFocusMemory
{
    private readonly Dictionary<OverlayDestination, OverlayFocusState> _states = new();

    /// <summary>Replaces one destination's semantic focus and scroll memory.</summary>
    /// <param name="destination">Destination whose state is saved.</param>
    /// <param name="semanticKey">Control identity; blank values become null.</param>
    /// <param name="scrollOffset">Vertical DIP offset; negative values clamp to zero.</param>
    internal void Remember(OverlayDestination destination, string? semanticKey, double scrollOffset)
    {
        _states[destination] = new OverlayFocusState(
            string.IsNullOrWhiteSpace(semanticKey) ? null : semanticKey,
            Math.Max(0, scrollOffset));
    }

    /// <summary>Reads remembered destination state without retaining controls.</summary>
    /// <param name="destination">Destination being reopened.</param>
    /// <returns>Saved state, or null focus and zero scroll when never recorded.</returns>
    internal OverlayFocusState Recall(OverlayDestination destination)
    {
        return _states.TryGetValue(destination, out var state)
            ? state
            : new OverlayFocusState(null, 0);
    }
}
