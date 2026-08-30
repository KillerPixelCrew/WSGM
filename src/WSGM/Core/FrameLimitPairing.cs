using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>How a frame limit relates to the panel's refresh rate.</summary>
/// <remarks>
/// A user setting rather than a fixed policy, because the right answer differs per device and per
/// tolerance for mode changes. A mode change is not free: an exclusive-fullscreen title can hitch,
/// minimize, or drop out across one.
/// </remarks>
public enum FrameLimitStrategy
{
    /// <summary>
    /// Cap frames and never touch the refresh rate. The default, and the right answer wherever
    /// variable refresh covers the range, because it changes no display state at all.
    /// </summary>
    FrameLimitOnly,

    /// <summary>
    /// Cap frames, and switch refresh only among the panel's own advertised modes.
    /// </summary>
    NativeModes,

    /// <summary>
    /// Cap frames, and pick the lowest mode the driver actually accepted that is an exact multiple
    /// of the cap — including modes synthesized beyond what the panel advertises.
    /// </summary>
    FrameDoubling,
}

/// <summary>
/// Chooses the refresh rate that goes with a frame cap, and the caps worth offering.
/// </summary>
/// <remarks>
/// In SteamOS the compositor resolves this pairing and the UI only displays the result. WSGM is the
/// backend on Windows, so the pairing is decided here and the refresh row shows what was chosen.
/// <para>
/// Every rate handed in must already have been discovered at runtime and accepted by the driver.
/// Nothing here may be hardcoded: the reference Claw accepts 30/48/60/75/100/120 while advertising
/// only 60 and 120, and a panel without variable refresh will likely accept only what it advertises.
/// </para>
/// </remarks>
public static class FrameLimitPairing
{
    /// <summary>Caps worth offering when the refresh rate is not being coupled to them.</summary>
    /// <remarks>
    /// A conventional ladder rather than every integer: a notch slider the user thumbs through with
    /// a stick needs stops that mean something, and 113 FPS is not one of them.
    /// </remarks>
    private static readonly int[] UncoupledLadder =
        [15, 20, 24, 30, 36, 40, 45, 50, 60, 72, 75, 90, 100, 120, 144, 165, 180, 240];

    /// <summary>Lowest cap worth offering at all.</summary>
    private const int MinimumCap = 15;

    /// <summary>
    /// The refresh rate to apply alongside a frame cap.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <param name="capFps">The frame cap, or zero for uncapped.</param>
    /// <param name="nativeHz">Refresh rates the panel itself advertises.</param>
    /// <param name="acceptedHz">Every rate the driver accepted, including synthesized ones.</param>
    /// <returns>
    /// The rate to set, or <see langword="null"/> when the refresh rate must be left alone — which
    /// is always the answer under <see cref="FrameLimitStrategy.FrameLimitOnly"/>, and the answer
    /// anywhere else when no available mode is an exact multiple of the cap.
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

        IReadOnlyList<int> candidates = strategy switch
        {
            FrameLimitStrategy.NativeModes => nativeHz,
            FrameLimitStrategy.FrameDoubling => acceptedHz,
            _ => [],
        };

        // The lowest exact multiple, because refresh rate is a power cost: a 30 FPS cap held at
        // 30 Hz costs meaningfully less than the same cap held at 120 Hz.
        return candidates
            .Where(hz => hz >= capFps && hz % capFps == 0)
            .OrderBy(hz => hz)
            .Select(hz => (int?)hz)
            .FirstOrDefault();
    }

    /// <summary>
    /// The frame caps worth offering under a strategy.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <param name="nativeHz">Refresh rates the panel itself advertises.</param>
    /// <param name="acceptedHz">Every rate the driver accepted, including synthesized ones.</param>
    /// <returns>
    /// The caps, ascending, with zero first for "off". Under a coupled strategy only caps that have
    /// an exact-cadence mode behind them appear, so every stop on the slider is one the backend can
    /// honour exactly.
    /// </returns>
    public static IReadOnlyList<int> FrameLimitOptions(
        FrameLimitStrategy strategy,
        IReadOnlyList<int> nativeHz,
        IReadOnlyList<int> acceptedHz
    )
    {
        IReadOnlyList<int> available = strategy switch
        {
            FrameLimitStrategy.NativeModes => nativeHz,
            FrameLimitStrategy.FrameDoubling => acceptedHz,
            _ => acceptedHz,
        };

        int ceiling = available.Count is 0 ? 0 : available.Max();
        if (ceiling < MinimumCap)
        {
            return [0];
        }

        if (strategy is FrameLimitStrategy.FrameLimitOnly)
        {
            return [0, .. UncoupledLadder.Where(cap => cap <= ceiling)];
        }

        // Every cap that divides some available mode exactly. Derived from the modes rather than
        // filtered from a ladder, so a panel with unusual rates still offers the caps it can
        // actually hold — 25 FPS is a real option on a panel that does 75 Hz.
        SortedSet<int> caps = [];
        foreach (int hz in available)
        {
            for (int cap = MinimumCap; cap <= hz; cap++)
            {
                if (hz % cap == 0)
                {
                    caps.Add(cap);
                }
            }
        }

        return [0, .. caps];
    }

    /// <summary>
    /// Whether the refresh-rate control should be offered to the user.
    /// </summary>
    /// <param name="strategy">The user's chosen strategy.</param>
    /// <returns><see langword="true"/> when the user owns the refresh rate.</returns>
    /// <remarks>
    /// Only under <see cref="FrameLimitStrategy.FrameLimitOnly"/>. Under the coupled strategies the
    /// pairing policy owns the refresh rate, and a second control would fight it — the user would
    /// set a rate and watch the next cap change overwrite it.
    /// </remarks>
    public static bool RefreshRateIsUserOwned(FrameLimitStrategy strategy) =>
        strategy is FrameLimitStrategy.FrameLimitOnly;
}
