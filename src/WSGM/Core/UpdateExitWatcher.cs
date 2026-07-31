using System;
using System.Threading;

namespace WSGM.Core;

/// <summary>Lets the (unelevated) installer ask a running — possibly elevated —
/// WSGM to exit before an update. taskkill can't touch an elevated process from
/// an unelevated setup, but signaling a named kernel event works: the event is
/// created by us with the default DACL (same user has full access) and no raised
/// integrity label. A graceful self-shutdown also runs the normal exit path, so
/// the Steam Input pin release fires.</summary>
public static class UpdateExitWatcher
{
    public const string EventName = @"Local\WSGM.ExitForUpdate";

    private static EventWaitHandle? _handle;

    public static void Start(Action onExitRequested)
    {
        try
        {
            _handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out _);
            var thread = new Thread(() =>
            {
                try
                {
                    _handle.WaitOne();
                    Log.Info("Exit requested by installer (update).");
                    onExitRequested();
                }
                catch
                {
                    // Watcher must never take the shell down.
                }
            })
            {
                IsBackground = true,
                Name = "WSGM.UpdateExit",
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"Update-exit watcher not available: {ex.Message}");
        }
    }
}
