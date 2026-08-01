using System;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Watches Steam by process-name polling (the Big Picture window lives in
/// steamwebhelper.exe, so name polling is authoritative). Raises SteamExited on the
/// UI thread when it transitions alive → dead.</summary>
public sealed class SteamMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly AliveEdgeDetector _edge = new();

    public event Action? SteamExited;

    public bool IsAlive { get; private set; }

    /// <summary>While true (desktop mode, or after the user deliberately closed
    /// Steam) an alive→dead transition is swallowed instead of raising SteamExited,
    /// so nothing auto-relaunches or pops the overlay.</summary>
    public bool Paused { get; set; }

    public SteamMonitor()
    {
        // The convenience ctor taking a callback auto-starts the timer (see
        // GamepadService) — keep construction and Start() explicit.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        IsAlive = Steam.IsRunning;

        // The detector only fires after a poll saw Steam alive, so the
        // seen-alive-once requirement is implied.
        if (_edge.Update(IsAlive))
        {
            if (Paused)
            {
                Log.Info("Steam exited (monitor paused, not reacting).");
            }
            else
            {
                Log.Info("Steam exited.");
                SteamExited?.Invoke();
            }
        }
    }

    public void Dispose() => _timer.Stop();
}
