using System;

namespace WSGM.Core;

/// <summary>Pure hold/release policy for the automatic download wake lock: acquire on
/// the first active sample, release only after a run of consecutive inactive polls so
/// a brief gap between queued items (or one unreachable poll during a Steam client
/// restart) does not flap the hold.</summary>
internal static class KeepAwakeDecider
{
    /// <summary>How many consecutive inactive polls it takes to drop the hold.</summary>
    internal const int ReleaseAfterInactivePolls = 2;

    /// <summary>Advances the policy by one poll sample.</summary>
    /// <param name="currentHold">Whether the download hold is currently active.</param>
    /// <param name="inactiveStreak">Consecutive inactive polls seen so far.</param>
    /// <param name="sampleActive">Whether this poll saw an active transfer; an
    /// unreachable poll counts as inactive.</param>
    /// <returns>The desired hold state and the updated streak.</returns>
    internal static (bool Hold, int InactiveStreak) Next(
        bool currentHold, int inactiveStreak, bool sampleActive)
    {
        if (sampleActive)
        {
            return (true, 0);
        }
        var streak = Math.Min(inactiveStreak + 1, ReleaseAfterInactivePolls);
        return (currentHold && streak < ReleaseAfterInactivePolls, streak);
    }
}
