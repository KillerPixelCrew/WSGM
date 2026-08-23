using System;
using System.Threading;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Lets the (elevated) installer ask a running — possibly elevated —
/// WSGM to exit before an update. An event created with the elevated token's
/// DEFAULT security is unreachable from a second WSGM instance, so the event is
/// created with an explicit descriptor scoped to THIS user plus BUILTIN\
/// Administrators (EVENT_MODIFY_STATE | SYNCHRONIZE) and a medium mandatory label:
/// the elevated setup and every same-user WSGM instance can wait/signal/reset,
/// while low-IL sandboxed processes cannot force an exit. A graceful self-shutdown
/// runs the normal exit path, asks elevated Steam to exit so an updated injected
/// payload can unload, then lets the Steam Input lease release and posture restore
/// fire too.</summary>
public static class UpdateExitWatcher
{
    // CROSS-VERSION CONTRACT — do not rename this event and do not narrow the
    // 0x00100002 grant below. During an update the event object is created by the
    // OLD running build; the new installer only opens it BY NAME and signals it
    // (installer\WSGM.iss:216, StopRunningInstances — OpenEventW with
    // EVENT_MODIFY_STATE, then SetEvent). If either drifts, a future upgrade can no
    // longer stop WSGM gracefully, Steam is never asked to exit, and the injected
    // Steam Input payload stays mapped. No test can cover that pairing — only an
    // actual installer run against an older build can.

    /// <summary>Gets the per-session event used by an updater to request a graceful exit.</summary>
    public const string EventName = @"Local\WSGM.ExitForUpdate";

    // The setup is ALWAYS elevated (PrivilegesRequired=admin): the user-SID ACE
    // covers every same-user WSGM instance (elevated or filtered token — the user
    // SID is never deny-only) and a setup elevated as this user; the BA ACE covers
    // a setup elevated via a different admin account. The medium label keeps the
    // unelevated settings instance able to wait/reset (it needs EVENT_MODIFY_STATE
    // for the stale-signal ResetEvent and the OpenEventW fallback below), while
    // low-IL/sandboxed processes can no longer force an exit. Internal rather than
    // private, and taking the SID rather than reading the token, so the "WD"
    // fallback is covered by a test instead of being asserted away.

    /// <summary>Builds the event's security descriptor: a DACL granting the given
    /// user SID and BUILTIN\Administrators EVENT_MODIFY_STATE | SYNCHRONIZE
    /// (0x00100002), plus a medium mandatory label with no-write-up.</summary>
    /// <param name="userSid">The current token's user SID in SDDL form, or
    /// <see langword="null"/> when it could not be read — practically impossible,
    /// and then that ACE falls back to the old Everyone ("WD") grant so an update
    /// can still stop this instance.</param>
    /// <returns>The SDDL string for <c>CreateEventW</c>'s security descriptor.</returns>
    internal static string BuildEventSddl(string? userSid)
        => $"D:(A;;0x00100002;;;{userSid ?? "WD"})(A;;0x00100002;;;BA)S:(ML;;NW;;;ME)";

    private static nint _event;

    /// <summary>Starts watching for the updater's graceful-exit request.</summary>
    /// <param name="onExitRequested">The callback that runs the normal application shutdown path.</param>
    public static void Start(Action onExitRequested)
    {
        try
        {
            string? userSid;
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                userSid = identity.User?.Value;
            }
            if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(BuildEventSddl(userSid), 1, out var sd, out _))
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
                // DACL grants this user only MODIFY_STATE|SYNCHRONIZE, so CreateEventW's
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
            // always correct (this user's grant includes EVENT_MODIFY_STATE).
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
                catch (Exception ex)
                {
                    // Watcher must never take the shell down — but a failed
                    // shutdown request is why the installer falls back to
                    // taskkill, so it has to be visible in the log.
                    Log.Warn($"Update-exit watcher: shutdown request failed: {ex.Message}");
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
