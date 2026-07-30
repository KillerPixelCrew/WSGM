using System;
using Avalonia.Threading;
using OpenFSE.Core;

namespace OpenFSE.Shell;

/// <summary>Watches the home app by process-name polling (Steam's window lives in a
/// different process than the launched exe, so name polling is authoritative).
/// Raises HomeAppExited on the UI thread when it transitions alive → dead.</summary>
public sealed class HomeAppMonitor : IDisposable
{
    private readonly HomeAppConfig _config;
    private readonly DispatcherTimer _timer;
    private bool _wasAlive;
    private bool _everAlive;
    private bool _elevationWarned;

    public event Action? HomeAppExited;

    public bool IsAlive { get; private set; }

    public HomeAppMonitor(HomeAppConfig config)
    {
        _config = config;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Poll());
        _timer.Start();
    }

    private void Poll()
    {
        var pids = WindowFinder.FindProcessIds(_config.ProcessNames);
        IsAlive = pids.Count > 0;

        if (IsAlive)
        {
            _everAlive = true;

            // One-time sanity check: is the home app actually elevated when it should be?
            if (_config.Elevated && !_elevationWarned)
            {
                foreach (var pid in pids)
                {
                    if (ElevationCheck.IsProcessElevated(pid) == false)
                    {
                        Log.Warn($"Home app process {pid} is running WITHOUT elevation " +
                                 "(was it already running before OpenFSE started it?).");
                        _elevationWarned = true;
                        break;
                    }
                }
            }
        }

        if (_wasAlive && !IsAlive && _everAlive)
        {
            Log.Info("Home app exited.");
            HomeAppExited?.Invoke();
        }
        _wasAlive = IsAlive;
    }

    public void Dispose() => _timer.Stop();
}
