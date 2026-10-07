using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Interop;

/// <summary>
///     Which registered power setting reported a display on/off transition.
///     <para>
///         Windows offers no way to <i>query</i> the current display power state from
///         user mode, so a notification is the only mechanism there is. WSGM therefore listens on
///         all three that exist and records which one spoke — a wake that one setting misses can
///         still arrive on another, and the source name in the log is what makes a missing
///         notification diagnosable from a pasted device log instead of guesswork.
///     </para>
/// </summary>
public enum DisplayStateSource
{
    /// <summary>
    ///     GUID_SESSION_DISPLAY_STATUS — the display of this session. The primary
    ///     source, the one Microsoft documents for interactive applications, and the only one
    ///     that may be trusted to say the screen went dark.
    /// </summary>
    Session,

    /// <summary>
    ///     GUID_CONSOLE_DISPLAY_STATE — the console session's display. Redundant
    ///     wake source only: it describes whichever session owns the console, so acting on
    ///     its "off" would mute the wrong session after a fast user switch.
    /// </summary>
    Console,

    /// <summary>
    ///     GUID_MONITOR_POWER_ON — the superseded pre-Windows-8 setting. Modern
    ///     Windows may never send it; treated as a best-effort wake source only.
    /// </summary>
    LegacyMonitor
}

/// <summary>
///     A raw message-only (HWND_MESSAGE) window whose queue is pumped by the
///     Avalonia UI thread. Hosts RegisterHotKey registrations and delivers display-state,
///     power-source, session lock, unlock and logoff, suspend and resume, shell-hook and volume
///     arrival and removal notifications.
/// </summary>
public sealed unsafe class MessageWindow : IDisposable
{
    /// <summary>The fixed POWERBROADCAST_SETTING header: the setting GUID and the payload length.</summary>
    private const int PowerSettingHeaderSize = 20;

    /// <summary>GUID_ACDC_POWER_SOURCE {5D3E9A59-E9D5-4B00-A6BD-FF34FF516548}.</summary>
    private static readonly Guid GuidAcDcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");

    private static MessageWindow? _instance;
    private PowerNotificationRegistration? _consoleDisplayNotify;
    private PowerNotificationRegistration? _displayNotify;

    /// <summary>How many subscribers asked for display-state notifications.</summary>
    private int _displaySubscribers;

    private PowerNotificationRegistration? _legacyDisplayNotify;
    private PowerNotificationRegistration? _powerSourceNotify;
    private bool _sessionNotify;
    private uint _shellHookMessage;
    private bool _shellHookRegistered;
    private PowerNotificationRegistration? _suspendResumeNotify;

    private nint _volumeNotify;

    // One window serves every consumer the composition hands it to, so volume notifications are
    // shared: each successful RegisterVolumeNotifications is matched by one Deregister, and only
    // the last one removes the registration another subscriber may still rely on.
    private int _volumeNotifyUsers;

    /// <summary>
    ///     Creates the process's message-only window on the UI thread, whose pump services it, and
    ///     registers the session, suspend/resume and power-source notifications. The composition
    ///     that constructs it owns it, passes it to its consumers and disposes it after them.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Another owner already holds the process window, or the native window could not be created.
    /// </exception>
    public MessageWindow()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_instance is not null)
        {
            throw new InvalidOperationException("The process message window already has an owner.");
        }

        Handle = CreateMessageOnlyWindow("WSGM.MessageWindow", &WndProc, "Failed to create message window");
        _instance = this;
        RegisterSessionNotifications();
        RegisterSuspendResumeNotifications();
        RegisterPowerSourceNotifications();
    }

    /// <summary>Gets the native handle of the message-only window.</summary>
    public nint Handle { get; private set; }

    /// <summary>
    ///     Ends every registration and destroys the native window on the UI thread. A window that
    ///     could not be destroyed keeps its handle and its dispatch slot, so it is never reported gone.
    /// </summary>
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Handle == 0)
        {
            return;
        }

        DeregisterShellHook();
        _displaySubscribers = 0;
        UnregisterDisplayStateNotifications();
        UnregisterSessionNotifications();
        UnregisterSuspendResumeNotifications();
        Release(ref _powerSourceNotify);
        UnregisterVolumeNotifications();
        if (!NativeMethods.DestroyWindow(Handle))
        {
            Log.Warn($"DestroyWindow(message window) failed (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        Handle = 0;
        if (_instance == this)
        {
            _instance = null;
        }
    }

    /// <summary>Raised on the Avalonia UI thread with the hotkey id.</summary>
    public event Action<int>? HotkeyPressed;

    /// <summary>
    ///     Raised on the Avalonia UI thread when a display turns on or off, with the
    ///     MONITOR_DISPLAY_STATE value (0 = off, 1 = on, 2 = dimmed) and which of the three
    ///     registered power settings reported it. Subscribers must weigh the source: only
    ///     <see cref="DisplayStateSource.Session" /> describes this session's own display.
    /// </summary>
    public event Action<int, DisplayStateSource>? DisplayStateChanged;

    /// <summary>
    ///     Raised on the Avalonia UI thread when this session's desktop is unlocked —
    ///     an independent "the user is back at a lit screen" signal for wakes where no display
    ///     notification is delivered.
    /// </summary>
    public event Action? SessionUnlocked;

    /// <summary>Raised on the Avalonia UI thread when this session's desktop is locked.</summary>
    /// <remarks>
    ///     The counterpart to <see cref="SessionUnlocked" />, and the point at which anything holding
    ///     hardware the user is no longer in front of should let go of it.
    /// </remarks>
    public event Action? SessionLocked;

    /// <summary>Raised on the Avalonia UI thread when this interactive session logs off.</summary>
    public event Action? SessionEnding;

    /// <summary>Raised on the Avalonia UI thread when the system is about to suspend.</summary>
    /// <remarks>
    ///     Delivered before the machine goes down and on a deadline Windows does not extend, so
    ///     subscribers must start their work and return rather than block this notification.
    ///     Reaches this window only through the suspend/resume registration made at creation:
    ///     Windows broadcasts the suspend and resume codes to top-level windows, and a message-only
    ///     window hears nothing of a sleep without it.
    /// </remarks>
    public event Action? SystemSuspending;

    /// <summary>Raised on the Avalonia UI thread when the system resumed from suspend.</summary>
    /// <remarks>
    ///     Raised for PBT_APMRESUMEAUTOMATIC, PBT_APMRESUMESUSPEND and PBT_APMRESUMECRITICAL alike.
    ///     Windows sends the first on every resume and adds the second only when the user caused it,
    ///     so a subscriber that listened for one of them would miss half the wakes; it can fire
    ///     several times for one resume and subscribers must be idempotent. The critical code is the
    ///     one a process gets when it never saw the suspend, which on a modern standby machine is the
    ///     normal way a hibernate ends rather than a failure.
    /// </remarks>
    public event Action? SystemResumed;

    /// <summary>Raised on the Avalonia UI thread when the system switches between AC and battery power.</summary>
    /// <remarks>
    ///     Carries no value: subscribers read the power status themselves, so there is one source for it.
    ///     Windows also sends the current source once right after registration, so subscribers must treat
    ///     a notification without a switch as harmless.
    /// </remarks>
    public event Action? PowerSourceChanged;

    /// <summary>
    ///     Raised on the Avalonia UI thread for a shell-hook notification.
    ///     Its delegate receives the HSHELL_* event code followed by the event-specific
    ///     lParam supplied by the shell.
    /// </summary>
    public event Action<nint, nint>? ShellHookReceived;

    /// <summary>
    ///     Raised on the Avalonia UI thread when any volume appeared or
    ///     disappeared. The argument is true for arrival, false for removal.
    /// </summary>
    /// <remarks>
    ///     Deliberately carries no device identity. The payload's device path would have
    ///     to be mapped back to a mount point, which is fragile and reader-specific;
    ///     rescanning drive letters answers the same question and works for every reader.
    ///     Subscribers must debounce and settle: the notification fires before Windows has
    ///     finished mounting the volume and assigning its letter.
    /// </remarks>
    public event Action<bool>? VolumeChanged;

    /// <summary>
    ///     Registers this window to receive shell-hook notifications.
    ///     The caller must later call <see cref="DeregisterShellHook" /> before a
    ///     different shell takes ownership of the desktop.
    /// </summary>
    /// <returns>True when the registration is active.</returns>
    public bool RegisterShellHook()
    {
        if (_shellHookRegistered)
        {
            return true;
        }

        _shellHookMessage = NativeMethods.RegisterWindowMessageW("SHELLHOOK");
        if (_shellHookMessage == 0)
        {
            Log.Warn($"RegisterWindowMessage(SHELLHOOK) failed (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        if (!NativeMethods.RegisterShellHookWindow(Handle))
        {
            Log.Warn($"RegisterShellHookWindow failed (error {Marshal.GetLastWin32Error()}).");
            _shellHookMessage = 0;
            return false;
        }

        _shellHookRegistered = true;
        Log.Info("Shell-hook window registered.");
        return true;
    }

    /// <summary>Stops this window receiving shell-hook notifications.</summary>
    public void DeregisterShellHook()
    {
        if (!_shellHookRegistered)
        {
            return;
        }

        if (!NativeMethods.DeregisterShellHookWindow(Handle))
        {
            Log.Warn($"DeregisterShellHookWindow failed (error {Marshal.GetLastWin32Error()}).");
        }

        _shellHookRegistered = false;
        _shellHookMessage = 0;
        Log.Info("Shell-hook window deregistered.");
    }

    /// <summary>
    ///     Subscribes this window to display on/off notifications. Idempotent;
    ///     safe to call when the feature toggle turns on at runtime.
    ///     <para>
    ///         THREE power settings are registered, not one.
    ///         <c>GUID_SESSION_DISPLAY_STATUS</c> is the primary and the only one that describes
    ///         this session's own display — it stays the sole source allowed to report the screen
    ///         going dark. <c>GUID_CONSOLE_DISPLAY_STATE</c> and the superseded
    ///         <c>GUID_MONITOR_POWER_ON</c> are redundant wake sources: a subscriber may act on
    ///         them only to undo something, never to start it. Registering the extras costs one
    ///         call each and a setting Windows never sends simply stays silent.
    ///     </para>
    /// </summary>
    /// <remarks>Reference-counted: every call is matched by one <see cref="DeregisterDisplayStateNotifications" />.</remarks>
    public void RegisterDisplayStateNotifications()
    {
        if (_displaySubscribers++ > 0)
        {
            return;
        }

        _displayNotify = TryRegisterSetting(NativeMethods.GuidSessionDisplayStatus, out var error);
        if (_displayNotify is null)
        {
            Log.Warn("RegisterPowerSettingNotification(session display status) failed "
                     + $"(error {error}).");
        }

        _consoleDisplayNotify = TryRegisterSetting(NativeMethods.GuidConsoleDisplayState, out _);
        _legacyDisplayNotify = TryRegisterSetting(NativeMethods.GuidMonitorPowerOn, out _);
        Log.Info($"Display-state notifications registered (session={_displayNotify is not null}, "
                 + $"console={_consoleDisplayNotify is not null}, legacy={_legacyDisplayNotify is not null}).");
    }

    /// <summary>Releases one subscriber's display on/off notifications; the last one stops them.</summary>
    public void DeregisterDisplayStateNotifications()
    {
        if (_displaySubscribers == 0 || --_displaySubscribers > 0)
        {
            return;
        }

        UnregisterDisplayStateNotifications();
    }

    private void UnregisterDisplayStateNotifications()
    {
        var any = _displayNotify is not null || _consoleDisplayNotify is not null
                                             || _legacyDisplayNotify is not null;
        if (!any)
        {
            return;
        }

        Release(ref _displayNotify);
        Release(ref _consoleDisplayNotify);
        Release(ref _legacyDisplayNotify);
        Log.Info("Display-state notifications deregistered.");
    }

    private void RegisterSessionNotifications()
    {
        _sessionNotify = NativeMethods.WTSRegisterSessionNotification(
            Handle,
            NativeMethods.NotifyForThisSession);
        if (!_sessionNotify)
        {
            Log.Warn("WTSRegisterSessionNotification failed "
                     + $"(error {Marshal.GetLastWin32Error()}).");
        }
    }

    private void UnregisterSessionNotifications()
    {
        if (!_sessionNotify)
        {
            return;
        }

        if (!NativeMethods.WTSUnRegisterSessionNotification(Handle))
        {
            Log.Warn("WTSUnRegisterSessionNotification failed "
                     + $"(error {Marshal.GetLastWin32Error()}).");
        }

        _sessionNotify = false;
    }

    // Suspend and resume are broadcast to top-level windows only, so this message-only window has
    // to ask for them explicitly, the way the volume interface is registered below. Without this
    // registration the SystemSuspending and SystemResumed events never fired.
    private void RegisterSuspendResumeNotifications()
    {
        if (_suspendResumeNotify is not null)
        {
            return;
        }

        try
        {
            _suspendResumeNotify = WindowsPower.RegisterSuspendResumeNotification(Handle);
        }
        catch (Win32Exception ex)
        {
            Log.Warn("RegisterSuspendResumeNotification failed "
                     + $"(error {ex.NativeErrorCode}) — a sleep will not suspend or resume the device cycle.");
            return;
        }

        Log.Info("Suspend/resume notifications registered.");
    }

    private void RegisterPowerSourceNotifications()
    {
        _powerSourceNotify = TryRegisterSetting(GuidAcDcPowerSource, out var error);
        if (_powerSourceNotify is null)
        {
            Log.Warn("RegisterPowerSettingNotification(AC/DC power source) failed "
                     + $"(error {error}): power preset assignments will not follow "
                     + "a switch between AC and battery.");
        }
    }

    private void UnregisterSuspendResumeNotifications()
    {
        Release(ref _suspendResumeNotify);
    }

    /// <summary>Registers one power-setting notification, or returns null with the native error.</summary>
    private PowerNotificationRegistration? TryRegisterSetting(Guid setting, out int error)
    {
        try
        {
            error = 0;
            return WindowsPower.RegisterSettingNotification(Handle, setting);
        }
        catch (Win32Exception ex)
        {
            error = ex.NativeErrorCode;
            return null;
        }
    }

    /// <summary>Unregisters a power notification by disposing it; Windows reports no release failure.</summary>
    private static void Release(ref PowerNotificationRegistration? registration)
    {
        registration?.Dispose();
        registration = null;
    }

    /// <summary>
    ///     Subscribes this window to volume arrival and removal. Reference-counted: pair each
    ///     successful call with one <see cref="DeregisterVolumeNotifications" />.
    /// </summary>
    /// <remarks>
    ///     This replaces guessing at a card reader's identity. The Playnite-era approach
    ///     watched WMI for a <c>Win32_DiskDrive</c> whose model matched a hard-coded
    ///     string, which only ever worked for the reader it was written against. A device-interface
    ///     registration for <c>GUID_DEVINTERFACE_VOLUME</c> is reader-agnostic, bus
    ///     agnostic and pure Win32.
    /// </remarks>
    /// <returns>True when the registration is active.</returns>
    public bool RegisterVolumeNotifications()
    {
        if (_volumeNotify != 0)
        {
            _volumeNotifyUsers++;
            return true;
        }

        var filter = new NativeMethods.DevBroadcastDeviceInterface
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.DevBroadcastDeviceInterface>(),
            DeviceType = NativeMethods.DbtDevTypDeviceInterface,
            ClassGuid = NativeMethods.GuidDevInterfaceVolume
        };
        _volumeNotify = NativeMethods.RegisterDeviceNotification(
            Handle, filter, NativeMethods.DeviceNotifyWindowHandle);
        if (_volumeNotify == 0)
        {
            Log.Warn("RegisterDeviceNotification(volume interface) failed "
                     + $"(error {Marshal.GetLastWin32Error()}); drive lists fall back to polling and card "
                     + "library installs are not watched.");
            return false;
        }

        _volumeNotifyUsers = 1;
        Log.Info("Volume arrival/removal notifications registered.");
        return true;
    }

    /// <summary>
    ///     Ends one <see cref="RegisterVolumeNotifications" /> claim; the window stops receiving
    ///     volume arrival and removal notifications when the last claim ends.
    /// </summary>
    public void DeregisterVolumeNotifications()
    {
        if (_volumeNotify == 0 || --_volumeNotifyUsers > 0)
        {
            return;
        }

        UnregisterVolumeNotifications();
    }

    private void UnregisterVolumeNotifications()
    {
        _volumeNotifyUsers = 0;
        if (_volumeNotify == 0)
        {
            return;
        }

        if (!NativeMethods.UnregisterDeviceNotification(_volumeNotify))
        {
            Log.Warn("UnregisterDeviceNotification(volume interface) failed "
                     + $"(error {Marshal.GetLastWin32Error()}).");
        }

        _volumeNotify = 0;
        Log.Info("Volume arrival/removal notifications deregistered.");
    }

    /// <summary>
    ///     Shared window-creation path for the process's message-only
    ///     (HWND_MESSAGE) windows. Class registration is idempotent:
    ///     ERROR_CLASS_ALREADY_EXISTS (1410) is benign — a re-create after a destroy
    ///     reuses the still-registered class. Any other registration failure is only
    ///     logged, because CreateWindowExW then fails on the unknown class and throws
    ///     <paramref name="failureMessage" /> anyway.
    /// </summary>
    /// <param name="className">Process-local class name; an existing registration must use the same procedure.</param>
    /// <param name="wndProc">Unmanaged procedure that must remain valid for the registered class lifetime.</param>
    /// <param name="failureMessage">Exception text used when CreateWindowExW fails.</param>
    /// <returns>Owned message-only HWND; the caller must destroy it on its creating thread.</returns>
    /// <exception cref="InvalidOperationException">The message-only window could not be created.</exception>
    internal static nint CreateMessageOnlyWindow(
        string className,
        delegate* unmanaged<nint, uint, nint, nint, nint> wndProc,
        string failureMessage)
    {
        var hInstance = NativeMethods.GetModuleHandleW(0);
        _ = RegisterWindowClass(className, wndProc);

        var hwnd = NativeMethods.CreateWindowExW(
            0, className, null, 0,
            0, 0, 0, 0,
            NativeMethods.HwndMessage, 0, hInstance, 0);
        return hwnd != 0 ? hwnd : throw new InvalidOperationException(failureMessage);
    }

    /// <summary>
    ///     Registers a native window class for this process. Re-registering an existing class is successful.
    /// </summary>
    /// <returns><see langword="true" /> when the class is available; otherwise, <see langword="false" />.</returns>
    /// <param name="className">Process-local class name; this helper does not replace a prior registration.</param>
    /// <param name="wndProc">Procedure pointer valid for the registered class lifetime.</param>
    internal static bool RegisterWindowClass(
        string className,
        delegate* unmanaged<nint, uint, nint, nint, nint> wndProc)
    {
        var hInstance = NativeMethods.GetModuleHandleW(0);
        var terminatedClassName = className + "\0";
        fixed (char* pClassName = terminatedClassName)
        {
            var wc = new NativeMethods.WndClassW
            {
                lpfnWndProc = wndProc,
                hInstance = hInstance,
                lpszClassName = (nint)pClassName
            };
            if (NativeMethods.RegisterClassW(&wc) != 0)
            {
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            if (error == 1410)
            {
                return true;
            }

            Log.Warn($"RegisterClassW({className}) failed (error {error}).");
            return false;
        }
    }

    /// <summary>
    ///     Decodes a POWERBROADCAST_SETTING this window registered for: the AC/DC source, or one of
    ///     the three display settings with its documented 4-byte DWORD state. Anything else, and
    ///     any buffer too short for what it declares, yields no notice.
    /// </summary>
    /// <param name="setting">The setting header and as much of its payload as the window may read.</param>
    /// <returns>What the broadcast reports, or the default notice when it reports nothing of ours.</returns>
    internal static PowerSettingNotice DecodePowerSetting(ReadOnlySpan<byte> setting)
    {
        if (setting.Length < PowerSettingHeaderSize)
        {
            return default;
        }

        var guid = new Guid(setting[..16]);
        if (guid == GuidAcDcPowerSource)
        {
            return new PowerSettingNotice(true, null, 0);
        }

        DisplayStateSource? source = null;
        if (guid == NativeMethods.GuidSessionDisplayStatus)
        {
            source = DisplayStateSource.Session;
        }
        else if (guid == NativeMethods.GuidConsoleDisplayState)
        {
            source = DisplayStateSource.Console;
        }
        else if (guid == NativeMethods.GuidMonitorPowerOn)
        {
            source = DisplayStateSource.LegacyMonitor;
        }

        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(setting.Slice(16, 4));
        if (source is null || dataLength < 4 || setting.Length < PowerSettingHeaderSize + 4)
        {
            return default;
        }

        return new PowerSettingNotice(false, source,
            BinaryPrimitives.ReadInt32LittleEndian(setting.Slice(PowerSettingHeaderSize, 4)));
    }

    /// <summary>Maps a WM_DEVICECHANGE event to volume arrival (true), removal (false) or neither (null).</summary>
    /// <param name="deviceEvent">The message's wParam.</param>
    /// <returns>Whether a volume arrived or left, or null for any other device event.</returns>
    internal static bool? DecodeVolumeChange(nint deviceEvent)
    {
        return deviceEvent switch
        {
            NativeMethods.DbtDeviceArrival => true,
            NativeMethods.DbtDeviceRemoveComplete => false,
            _ => null
        };
    }

    // POWERBROADCAST_SETTING is a GUID, a DWORD DataLength and DataLength payload bytes. Only the
    // header and the first DWORD of the payload are ever read, so only those are exposed.
    private static ReadOnlySpan<byte> ReadPowerSetting(nint lParam)
    {
        var header = new ReadOnlySpan<byte>((void*)lParam, PowerSettingHeaderSize);
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
        return new ReadOnlySpan<byte>((void*)lParam, PowerSettingHeaderSize + (dataLength < 4 ? (int)dataLength : 4));
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            var instance = _instance;
            if (instance is null)
            {
                return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
            }

            switch (msg)
            {
                case NativeMethods.WmHotkey:
                {
                    var id = (int)wParam;
                    Dispatcher.UIThread.Post(() => instance.HotkeyPressed?.Invoke(id));
                    return 0;
                }
                case NativeMethods.WmPowerBroadcast
                    when wParam == NativeMethods.PbtPowerSettingChange && lParam != 0:
                {
                    var notice = DecodePowerSetting(ReadPowerSetting(lParam));
                    if (notice.PowerSourceChanged)
                    {
                        Dispatcher.UIThread.Post(() => instance.PowerSourceChanged?.Invoke());
                    }
                    else if (notice.DisplaySource is { } reported)
                    {
                        var state = notice.DisplayState;
                        Dispatcher.UIThread.Post(() => instance.DisplayStateChanged?.Invoke(state, reported));
                    }

                    return 1;
                }
                case NativeMethods.WmPowerBroadcast when wParam == NativeMethods.PbtApmSuspend:
                    Dispatcher.UIThread.Post(() => instance.SystemSuspending?.Invoke());
                    return 1;
                case NativeMethods.WmPowerBroadcast
                    when wParam is NativeMethods.PbtApmResumeAutomatic or NativeMethods.PbtApmResumeSuspend
                        or NativeMethods.PbtApmResumeCritical:
                    Dispatcher.UIThread.Post(() => instance.SystemResumed?.Invoke());
                    return 1;
                case NativeMethods.WmWtsSessionChange when instance._sessionNotify:
                    switch (wParam)
                    {
                        case NativeMethods.WtsSessionLock:
                            Dispatcher.UIThread.Post(() => instance.SessionLocked?.Invoke());
                            return 0;
                        case NativeMethods.WtsSessionUnlock:
                            Dispatcher.UIThread.Post(() => instance.SessionUnlocked?.Invoke());
                            return 0;
                        case NativeMethods.WtsSessionLogoff:
                            Dispatcher.UIThread.Post(() => instance.SessionEnding?.Invoke());
                            return 0;
                    }

                    break;
                case NativeMethods.WmDeviceChange
                    when instance._volumeNotify != 0 && DecodeVolumeChange(wParam) is { } arrived:
                    // The payload is not read: see the VolumeChanged remarks. Returning
                    // TRUE is the documented answer for a device event that is not a
                    // removal QUERY, which this window never registers for.
                    Dispatcher.UIThread.Post(() => instance.VolumeChanged?.Invoke(arrived));
                    return 1;
            }

            if (msg != instance._shellHookMessage || !instance._shellHookRegistered)
            {
                return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
            }

            Dispatcher.UIThread.Post(() => instance.ShellHookReceived?.Invoke(wParam, lParam));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("MessageWindow message failed", ex);
            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }
}

/// <summary>What one decoded power-setting broadcast reports to the message window.</summary>
/// <param name="PowerSourceChanged">Whether the AC/DC power source setting spoke.</param>
/// <param name="DisplaySource">Which display setting spoke, or null when none did.</param>
/// <param name="DisplayState">The MONITOR_DISPLAY_STATE value the display setting reported.</param>
internal readonly record struct PowerSettingNotice(
    bool PowerSourceChanged,
    DisplayStateSource? DisplaySource,
    int DisplayState);
