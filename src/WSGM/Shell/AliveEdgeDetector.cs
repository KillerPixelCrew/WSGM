namespace WSGM.Shell;

/// <summary>Alive → dead edge detection shared by the process-name pollers
/// (SteamMonitor, StartupAppWatcher). Update() records the polled state and
/// returns true only on the alive → dead transition. The internal flag can only
/// be true after a poll saw the process alive, so the seen-alive-once
/// requirement is implied.</summary>
public sealed class AliveEdgeDetector
{
    private bool _wasAlive;

    /// <summary>Records the polled state; true iff the previous poll saw the
    /// process alive and this one did not.</summary>
    public bool Update(bool alive)
    {
        var exited = _wasAlive && !alive;
        _wasAlive = alive;
        return exited;
    }
}
