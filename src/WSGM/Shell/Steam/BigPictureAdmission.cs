using System.Diagnostics;

namespace WSGM.Shell;

/// <summary>Admits Steam's Big Picture window once per browser process for the game-mode gate.</summary>
/// <remarks>
///     A cold start must show its window before anything touches its CEF context (the device-observed
///     startup failure). After that, a window missing for one 1 s sample used to close the transport,
///     detach every socket and leave WSGM's namespaces in Steam unanswered until the full reapply: a
///     window recreated across a display change or a wake cost a complete teardown. Now only a window
///     missing for <see cref="SteamUiReadiness.BigPictureLossGrace" />, a new browser process or a hold
///     withdraws the admission. The gate worker is the sole owner.
/// </remarks>
internal sealed class BigPictureAdmission
{
    private uint _admittedBrowser;
    private long _missingSince;

    /// <summary>Decides from one observation whether Big Picture is ready.</summary>
    /// <param name="steam">The observation.</param>
    /// <param name="held">Whether a hold is in force, which withdraws the admission.</param>
    /// <param name="now">The observation time, in <see cref="Stopwatch" /> ticks.</param>
    /// <returns>Whether the game-mode gate may treat Big Picture as up.</returns>
    internal bool Observe(SteamProcessSnapshot steam, bool held, long now)
    {
        if (steam.BigPictureWindow)
        {
            // A window seen during a hold opens a held request but earns no grace for later.
            _admittedBrowser = held ? 0 : steam.BrowserProcessId;
            _missingSince = 0;
            return true;
        }

        if (held || !steam.Running || _admittedBrowser == 0 || _admittedBrowser != steam.BrowserProcessId)
        {
            _admittedBrowser = 0;
            return false;
        }

        if (_missingSince == 0)
        {
            _missingSince = now;
        }

        if (Stopwatch.GetElapsedTime(_missingSince, now) < SteamUiReadiness.BigPictureLossGrace)
        {
            return true;
        }

        _admittedBrowser = 0;
        return false;
    }
}
