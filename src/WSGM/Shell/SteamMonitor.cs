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
    private bool _wasAlive;
    private bool _everAlive;

    public event Action? SteamExited;

    public bool IsAlive { get; private set; }

    /// <summary>While true (desktop mode, or after the user deliberately closed
    /// Steam) an alive→dead transition is swallowed instead of raising SteamExited,
    /// so nothing auto-relaunches or pops the overlay.</summary>
    public bool Paused { get; set; }

    public SteamMonitor()
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Poll());
        _timer.Start();
    }

    private void Poll()
    {
        IsAlive = Steam.IsRunning;
        if (IsAlive)
        {
            _everAlive = true;
        }

        if (_wasAlive && !IsAlive && _everAlive)
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
        _wasAlive = IsAlive;
    }

    public void Dispose() => _timer.Stop();
}
