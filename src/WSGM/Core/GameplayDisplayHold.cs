using System;

namespace WSGM.Core;

/// <summary>
/// Holds Windows' display awake while a game is running, and only while one is.
/// </summary>
/// <remarks>
/// Windows feeds its idle timer from the raw keyboard and mouse stream. A HID gamepad is not part
/// of it, so a controller-only session looks idle no matter how hard it is being played, and the
/// display timeout expires on top of the game. Measured on the reference handheld on 2026-09-10:
/// with Big Picture in the foreground, <c>powercfg /requests</c> listed no DISPLAY request from
/// Steam or from the title, and the display idle timeout read 60 seconds on both power sources.
/// <para>
/// The hold is scoped to a running application and released the moment there is not one, so it can
/// never become "keep awake while WSGM runs". That is the whole difference between this and the
/// manual Keep Awake on the Power tab, which is the user's own switch and unaffected by this.
/// </para>
/// <para>
/// Display rather than system: the complaint is the screen going dark mid-game. A system request
/// would also stop standby, which the user may legitimately want on a timer even while a game sits
/// paused, and taking that decision away is not this fix's business.
/// </para>
/// </remarks>
public sealed class GameplayDisplayHold : IDisposable
{
    private readonly WakeLock _lock = new("WSGM: a game is running", requestType: 0);
    private bool _disposed;

    /// <summary>Whether the display request is currently held.</summary>
    public bool IsHeld => _lock.IsHeld;

    /// <summary>Applies the hold for the current running-application state.</summary>
    /// <param name="gameRunning">Whether a game is running in the foreground right now.</param>
    /// <remarks>
    /// Idempotent: the underlying request is acquired and released once each, so repeated
    /// observations of the same state do not touch Windows.
    /// </remarks>
    public void Apply(bool gameRunning)
    {
        if (_disposed || gameRunning == _lock.IsHeld)
        {
            return;
        }

        if (gameRunning)
        {
            if (_lock.Acquire())
            {
                Log.Change("power.gameplay-display", "A game is running; holding the display awake.");
            }
            return;
        }

        _lock.Release();
        Log.Change("power.gameplay-display", "No game is running; the display may sleep again.");
    }

    /// <summary>Releases the request and the native handle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Release();
        _lock.Dispose();
    }
}
