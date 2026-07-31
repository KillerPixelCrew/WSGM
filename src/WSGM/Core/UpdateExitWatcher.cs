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
/// path, so the Steam Input pin release and posture restore fire too.</summary>
public static class UpdateExitWatcher
{
    public const string EventName = @"Local\WSGM.ExitForUpdate";

    // D: Everyone -> EVENT_MODIFY_STATE | SYNCHRONIZE (0x00100002); S: low
    // mandatory label, no-write-up policy (low is below every caller, so anyone
    // may signal).
    private const string EventSddl = "D:(A;;0x00100002;;;WD)S:(ML;;NW;;;LW)";

    private static nint _event;

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
                // implicit EVENT_ALL_ACCESS open is denied. SYNCHRONIZE is all a waiter
                // needs — device-confirmed 'CreateEvent failed' in the field.
                _event = NativeMethods.OpenEventW(NativeMethods.Synchronize, false, EventName);
                if (_event == 0)
                {
                    Log.Warn($"Update-exit watcher: OpenEvent fallback failed (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
                    return;
                }
                Log.Info("Update-exit watcher: opened existing event (SYNCHRONIZE).");
            }
            else if (_event == 0)
            {
                Log.Warn($"Update-exit watcher: CreateEvent failed (error {createError}).");
                return;
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
