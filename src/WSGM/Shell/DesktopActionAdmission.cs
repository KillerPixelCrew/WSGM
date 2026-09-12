namespace WSGM.Shell;

/// <summary>Coalesces desktop lifecycle notifications without retrying uncertain plugin actions.</summary>
internal sealed class DesktopActionAdmission
{
    private readonly object _gate = new();
    private bool _busy;
    private long? _lastStarted;

    internal bool TryBegin(bool gameMode, bool transitioning, long elapsedMilliseconds)
    {
        lock (_gate)
        {
            if (gameMode || transitioning || _busy
                || (_lastStarted is { } last && elapsedMilliseconds - last < 5000)) { return false; }
            _busy = true;
            _lastStarted = elapsedMilliseconds;
            return true;
        }
    }

    internal void End() { lock (_gate) { _busy = false; } }
}
