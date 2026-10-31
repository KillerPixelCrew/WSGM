using System;
using System.Globalization;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>AutoTDP's frame-rate target as the overlay and Quick Access both draw it.</summary>
/// <param name="Fps">The resolved profile target, or the default when no layer sets one.</param>
/// <param name="EffectiveFps">The rate control aims for, lower than <paramref name="Fps" /> under a lower frame limit.</param>
/// <param name="GameOverride">Whether the running game's profile supplies the target.</param>
internal sealed record AutoTdpTargetView(int Fps, int EffectiveFps, bool GameOverride)
{
    /// <summary>The line under the slider: what the target does, or the frame limit holding it lower.</summary>
    internal string Description => EffectiveFps < Fps
        ? string.Create(CultureInfo.InvariantCulture, $"Held to the {EffectiveFps} FPS frame limit.")
        : "Power drops until the game runs just above this rate.";

    /// <summary>Resolves the target through the profile layers and the service's current deadline.</summary>
    /// <param name="layers">The profile layers in force.</param>
    /// <param name="availability">The service's admission state, whose deadline includes a lower frame limit.</param>
    /// <returns>The view.</returns>
    internal static AutoTdpTargetView Resolve(ProfileLayers layers, AutoTdpAvailability? availability)
    {
        var stored = layers.Value(values => values.AutoTdpTargetFps);
        var fps = AutoTdpTarget.EffectiveFps(stored.Value, null);
        var effective = availability?.TargetFrametimeMs is > 0 and { } deadline
            ? Math.Min(fps, (int)Math.Round(1000d / deadline))
            : fps;
        return new AutoTdpTargetView(fps, effective, stored.IsGameOverride);
    }
}
