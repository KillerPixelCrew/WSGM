using System;
using System.Threading;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Lets the (unelevated) installer ask a running — possibly elevated —
/// WSGM to exit before an update. taskkill can't touch an elevated process from
/// an unelevated setup, and an event created with the elevated token's DEFAULT
/// security is just as unreachable: its DACL grants BUILTIN\Administrators (a
/// deny-only SID in the installer's filtered token) and it carries a high
/// integrity label. So the event is created with an explicit descriptor —
/// Everyone gets EVENT_MODIFY_STATE | SYNCHRONIZE, low mandatory label so
/// lower-IL processes may signal. A graceful self-shutdown runs the normal exit
/// path, asks elevated Steam to exit so an updated injected payload can unload,
/// then lets the Steam Input lease release and posture restore fire too.</summary>
public static class UpdateExitWatcher
{
    /// <summary>Gets the per-session event used by an updater to request a graceful exit.</summary>
    public const string EventName = @"Local\WSGM.ExitForUpdate";

    // D: Everyone -> EVENT_MODIFY_STATE | SYNCHRONIZE (0x00100002); S: low
    // mandatory label, no-write-up policy (low is below every caller, so anyone
    // may signal).
    private const string EventSddl = "D:(A;;0x00100002;;;WD)S:(ML;;NW;;;LW)";

    private static nint _event;

    /// <summary>Starts watching for the updater's graceful-exit request.</summary>
    /// <param name="onExitRequested">The callback that runs the normal application shutdown path.</param>
    public static void Start(Action onExitRequested)
    {
        try
        {
            if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(EventSddl, 1, out var sd, out _))
            {
                Log.Warn($"Update-exit watcher: SDDL conversion failed (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
                return;
            }
            int createError;
            try
            {
                var attributes = new NativeMethods.SecurityAttributes
                {
                    nLength = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SecurityAttributes>(),
                    lpSecurityDescriptor = sd,
                    bInheritHandle = 0,
                };
                // Manual-reset: the installer signals exactly once, and EVERY waiting
                // instance (elevated shell + settings window) must be released by it.
                _event = NativeMethods.CreateEventW(ref attributes, manualReset: true, initialState: false, EventName);
                createError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            }
            finally
            {
                NativeMethods.LocalFree(sd);
            }
            if (_event == 0 && createError == 5 /* ERROR_ACCESS_DENIED */)
            {
                // The event already exists (another WSGM instance created it) and its
                // DACL grants Everyone only MODIFY_STATE|SYNCHRONIZE, so CreateEventW's
                // implicit EVENT_ALL_ACCESS open is denied. SYNCHRONIZE to wait plus
                // MODIFY_STATE for the stale-signal reset below is all a watcher
                // needs — device-confirmed 'CreateEvent failed' in the field.
                _event = NativeMethods.OpenEventW(NativeMethods.Synchronize | NativeMethods.EventModifyState, false, EventName);
                if (_event == 0)
                {
                    Log.Warn($"Update-exit watcher: OpenEvent fallback failed (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
                    return;
                }
                Log.Info("Update-exit watcher: opened existing event (MODIFY_STATE|SYNCHRONIZE).");
            }
            else if (_event == 0)
            {
                Log.Warn($"Update-exit watcher: CreateEvent failed (error {createError}).");
                return;
            }

            // A manual-reset event stays signaled for as long as ANY handle keeps the
            // kernel object alive. After an update, the old instance's slow graceful
            // teardown can carry the installer's signal past the relaunch; without
            // this reset the fresh instance would see that stale signal and shut
            // itself down immediately — update "done", no shell running. Any signal
            // present at watcher start predates this process, so clearing it is
            // always correct (Everyone's grant includes EVENT_MODIFY_STATE).
            if (!NativeMethods.ResetEvent(_event))
            {
                Log.Warn($"Update-exit watcher: could not clear stale signal (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
            }

            var thread = new Thread(() =>
            {
                try
                {
                    NativeMethods.WaitForSingleObject(_event, uint.MaxValue);
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
