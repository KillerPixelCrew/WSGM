using System.Threading;

namespace WSGM.Shell;

/// <summary>Coalesces desktop lifecycle notifications without retrying uncertain plugin actions.</summary>
internal sealed class DesktopActionAdmission
{
    private readonly Lock _gate = new();
    private bool _busy;
    private long? _lastStarted;

    /// <summary>Claims the desktop action sequence when idle and outside the five-second coalescing window.</summary>
    /// <param name="gameMode">Whether game-mode ownership is active.</param>
    /// <param name="transitioning">Whether a mode transition is in progress.</param>
    /// <param name="elapsedMilliseconds">Monotonic timestamp in milliseconds, from the same clock on every call.</param>
    /// <returns>True when admission was claimed; call End exactly once after an accepted attempt.</returns>
    internal bool TryBegin(bool gameMode, bool transitioning, long elapsedMilliseconds)
    {
        lock (_gate)
        {
            if (gameMode || transitioning || _busy
                || (_lastStarted is { } last && elapsedMilliseconds - last < 5000))
            {
                return false;
            }

            _busy = true;
            _lastStarted = elapsedMilliseconds;
            return true;
        }
    }

    /// <summary>Releases the active claim while retaining the last-start timestamp for coalescing.</summary>
    internal void End()
    {
        lock (_gate)
        {
            _busy = false;
        }
    }
}
