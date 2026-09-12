using System;

namespace WSGM.Shell;

/// <summary>When the splash is allowed to give up waiting for Big Picture.</summary>
internal static class SplashPolicy
{
    /// <summary>How long to wait for the Big Picture window after Steam has been asked for it.</summary>
    internal static readonly TimeSpan SteamTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Whether the splash should close because Big Picture never appeared.
    ///
    /// The clock starts when Steam is actually asked, not when the cover goes up. A Game Mode entry
    /// can hold the splash for as long as it takes a person to reach a TV, and a timeout measured
    /// from the cover would fire in the middle of exactly the wait the cover exists for.</summary>
    /// <param name="armed">Whether Big Picture has been requested yet.</param>
    /// <param name="sinceArmed">How long ago it was requested.</param>
    /// <returns>True when the splash should close itself.</returns>
    internal static bool ShouldTimeout(bool armed, TimeSpan sinceArmed) => armed && sinceArmed > SteamTimeout;
}
