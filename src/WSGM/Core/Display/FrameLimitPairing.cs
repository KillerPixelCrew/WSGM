using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>How a frame limit relates to the panel's refresh rate.</summary>
/// <remarks>
///     A user setting rather than a fixed policy, because the right answer differs per device and per
///     tolerance for mode changes. A mode change is not free: an exclusive-fullscreen title can hitch,
///     minimize, or drop out across one.
/// </remarks>
public enum FrameLimitStrategy
{
    /// <summary>
    ///     Cap frames and never touch the refresh rate. The default, and the right answer wherever
    ///     variable refresh covers the range, because it changes no display state at all.
    /// </summary>
    FrameLimitOnly,

    /// <summary>
    ///     Cap frames, and switch refresh only among the panel's own advertised modes.
    /// </summary>
    NativeModes,

    /// <summary>
    ///     Cap frames, and pick the lowest driver-accepted mode that shows every frame at least twice —
    ///     including modes synthesized beyond what the panel advertises. A doubled cadence is what lets
    ///     adaptive sync's low-framerate compensation smooth the presentation; holding a 30 FPS cap at
    ///     30 Hz keeps that machinery out of reach. When no doubled multiple exists the lowest exact
    ///     multiple still wins, and failing that the lowest mode that can present the cap.
    /// </summary>
    FrameDoubling
}

/// <summary>
///     Chooses the refresh rate that goes with a frame cap, and the caps worth offering.
/// </summary>
/// <remarks>
///     In SteamOS the compositor resolves this pairing and the UI only displays the result. WSGM is the
///     backend on Windows, so the pairing is decided here and the refresh row shows what was chosen.
///     <para>
///         Every rate handed in must already have been discovered at runtime and accepted by the driver.
///         Nothing here may be hardcoded: the reference Claw accepts 30/48/60/75/100/120 while advertising
///         only 60 and 120, and a panel without variable refresh will likely accept only what it advertises.
///     </para>
/// </remarks>
public static class FrameLimitPairing
{
    /// <summary>Lowest cap the slider offers, under every strategy.</summary>
    /// <remarks>
    ///     Higher than <see cref="MinimumCap" /> on purpose. The cap is a free number rather than a
    ///     cadence stop, so the only question left is what is worth playing at: below 30 FPS is not.
    /// </remarks>
    private const int UncoupledFloor = 30;

    /// <summary>Lowest cap worth offering at all.</summary>
    private const int MinimumCap = 15;

    /// <summary>
    ///     The refresh rate to apply alongside a frame cap.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <param name="capFps">The frame cap, or zero for uncapped.</param>
    /// <param name="nativeHz">Refresh rates the panel itself advertises.</param>
    /// <param name="acceptedHz">Every rate the driver accepted, including synthesized ones.</param>
    /// <returns>
    ///     The preferred exact multiple, or the lowest candidate at least as high as the cap when no
    ///     exact multiple exists. FrameDoubling prefers an exact multiple of at least twice the cap.
    ///     Returns <see langword="null" /> for FrameLimitOnly, a cap below the supported minimum, or
    ///     when the selected strategy has no candidate that can present the cap.
    /// </returns>
    public static int? SelectRefreshHz(
        FrameLimitStrategy strategy,
        int capFps,
        IReadOnlyList<int> nativeHz,
        IReadOnlyList<int> acceptedHz
    )
    {
        if (strategy is FrameLimitStrategy.FrameLimitOnly || capFps < MinimumCap)
        {
            return null;
        }

        var candidates = strategy switch
        {
            FrameLimitStrategy.NativeModes => nativeHz,
            FrameLimitStrategy.FrameDoubling => acceptedHz,
            _ => []
        };

        // Prefer a doubled cadence for FrameDoubling, then minimize refresh to limit power cost.
        if (strategy is FrameLimitStrategy.FrameDoubling)
        {
            var doubled = candidates
                .Where(hz => hz % capFps == 0 && hz >= capFps * 2)
                .OrderBy(hz => hz)
                .Select(hz => (int?)hz)
                .FirstOrDefault();
            if (doubled is not null)
            {
                return doubled;
            }
        }

        // Exact cadence is preferred before the noninteger fallback under either coupled strategy.
        var exact = candidates
            .Where(hz => hz >= capFps && hz % capFps == 0)
            .OrderBy(hz => hz)
            .Select(hz => (int?)hz)
            .FirstOrDefault();
        if (exact is not null)
        {
            return exact;
        }

        // Arbitrary caps still need a mode; choose the lowest candidate that can present every frame.
        return candidates
            .Where(hz => hz >= capFps)
            .OrderBy(hz => hz)
            .Select(hz => (int?)hz)
            .FirstOrDefault();
    }

    /// <summary>The lowest and highest frame cap the panel can be asked for.</summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <param name="nativeHz">Refresh rates the panel itself advertises.</param>
    /// <param name="acceptedHz">Every rate the driver accepted, including synthesized ones.</param>
    /// <returns>The inclusive range, or null when the panel cannot hold a cap worth offering.</returns>
    /// <remarks>
    ///     Every strategy offers a continuous integer range with a 30 FPS floor. NativeModes uses the
    ///     advertised-rate ceiling; the other strategies use the accepted-rate ceiling. Refresh pairing
    ///     chooses a supported mode independently of the chosen cap.
    /// </remarks>
    public static (int Minimum, int Maximum)? FrameLimitRange(
        FrameLimitStrategy strategy,
        IReadOnlyList<int> nativeHz,
        IReadOnlyList<int> acceptedHz
    )
    {
        var available = strategy switch
        {
            FrameLimitStrategy.NativeModes => nativeHz,
            _ => acceptedHz
        };

        var ceiling = available.Count is 0 ? 0 : available.Max();
        return ceiling < UncoupledFloor ? null : (UncoupledFloor, ceiling);
    }

    /// <summary>
    ///     The frame caps worth offering under a strategy.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <param name="nativeHz">Refresh rates the panel itself advertises.</param>
    /// <param name="acceptedHz">Every rate the driver accepted, including synthesized ones.</param>
    /// <returns>
    ///     Zero first for "off", followed by every integer in the inclusive FrameLimitRange under
    ///     every strategy. Returns only zero when no playable range is available; refresh pairing,
    ///     rather than the cap list, chooses the closest supported cadence.
    /// </returns>
    public static IReadOnlyList<int> FrameLimitOptions(
        FrameLimitStrategy strategy,
        IReadOnlyList<int> nativeHz,
        IReadOnlyList<int> acceptedHz
    )
    {
        if (FrameLimitRange(strategy, nativeHz, acceptedHz) is not { } range)
        {
            return [0];
        }

        // Zero is the uncapped sentinel; actual slider bounds come from FrameLimitRange.
        List<int> caps = [0];
        for (var cap = range.Minimum; cap <= range.Maximum; cap++)
        {
            caps.Add(cap);
        }

        return caps;
    }

    /// <summary>
    ///     Whether the refresh-rate control should be offered to the user.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <returns><see langword="true" /> when the user owns the refresh rate.</returns>
    /// <remarks>
    ///     Only under <see cref="FrameLimitStrategy.FrameLimitOnly" />. Under the coupled strategies the
    ///     pairing policy owns the refresh rate, and a second control would fight it — the user would
    ///     set a rate and watch the next cap change overwrite it.
    /// </remarks>
    public static bool RefreshRateIsUserOwned(FrameLimitStrategy strategy)
    {
        return strategy is FrameLimitStrategy.FrameLimitOnly;
    }
}
