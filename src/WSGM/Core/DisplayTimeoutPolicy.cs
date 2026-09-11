using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Keeps the display from turning off before Steam's screensaver is allowed to start.</summary>
/// <remarks>
/// Steam's Big Picture screensaver runs on its own idle timeout, and the display-off timeout has to
/// be the later of the two or the screensaver never shows. Steam pairs its timeouts with power
/// sources the way its SteamOS Power page lays them out: the plugged-in screensaver timeout bounds
/// the plugged-in display timeout and the battery one bounds the battery display timeout. On a
/// machine Steam believes has no battery it shows only the plugged-in timeout and that one applies
/// whatever the power source, so it bounds both. A screensaver timeout of zero is disabled and bounds
/// nothing; a display timeout of zero is never and satisfies any bound.
/// </remarks>
internal static class DisplayTimeoutPolicy
{
    /// <summary>The two timeouts this policy governs, in the order Steam's Power page lists them.</summary>
    internal static readonly PowerTimeoutKind[] DisplayKinds = [PowerTimeoutKind.DisplayDc, PowerTimeoutKind.DisplayAc];

    /// <summary>The screensaver timeout that bounds one display timeout.</summary>
    /// <param name="kind">The display timeout.</param>
    /// <param name="steam">Steam's last reported screensaver timeouts, or null before any report.</param>
    /// <returns>The minimum in seconds, or null when nothing bounds it.</returns>
    internal static int? Minimum(PowerTimeoutKind kind, SteamScreensaverReport? steam)
    {
        if (steam is null)
        {
            return null;
        }

        int seconds = kind switch
        {
            PowerTimeoutKind.DisplayAc => steam.PluggedInSeconds,
            PowerTimeoutKind.DisplayDc => steam.Battery && steam.BatterySeconds is int battery
                ? battery
                : steam.PluggedInSeconds,
            _ => 0,
        };
        return seconds > 0 ? seconds : null;
    }

    /// <summary>Whether a display timeout keeps the display on until the screensaver may start.</summary>
    /// <param name="seconds">The display timeout; zero means never.</param>
    /// <param name="minimum">The bound, or null for none.</param>
    /// <returns>True when the order holds.</returns>
    internal static bool Allows(int seconds, int? minimum) =>
        minimum is null || seconds == 0 || seconds >= minimum.Value;

    /// <summary>The value a display timeout below its bound is raised to.</summary>
    /// <param name="minimum">The bound in seconds.</param>
    /// <returns>The shortest preset at or above the bound, or the bound itself beyond the presets.</returns>
    internal static int Raised(int minimum) =>
        PowerTimeouts.PresetsSeconds.Where(preset => preset >= minimum).DefaultIfEmpty(minimum).Min();

    /// <summary>The next preset after <paramref name="current"/> that the bound allows.</summary>
    /// <param name="current">The current display timeout.</param>
    /// <param name="minimum">The bound, or null for none.</param>
    /// <returns>The preset to cycle to.</returns>
    internal static int NextAllowed(int current, int? minimum)
    {
        int next = current;
        foreach (int _ in PowerTimeouts.PresetsSeconds)
        {
            next = PowerTimeouts.NextPreset(next);
            if (Allows(next, minimum))
            {
                return next;
            }
        }

        return 0;
    }

    /// <summary>Every choice a display timeout may take: the allowed presets and the current value.</summary>
    /// <param name="current">The current display timeout, listed even when it is not a preset.</param>
    /// <param name="minimum">The bound, or null for none.</param>
    /// <returns>Shortest first, never last.</returns>
    internal static IReadOnlyList<int> Choices(int current, int? minimum)
    {
        IEnumerable<int> timed = PowerTimeouts.PresetsSeconds
            .Where(preset => preset > 0 && Allows(preset, minimum))
            .Append(current)
            .Where(seconds => seconds > 0)
            .Distinct()
            .Order();
        return [.. timed, 0];
    }
}
