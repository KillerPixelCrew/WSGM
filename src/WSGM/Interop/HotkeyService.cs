using System;
using System.Runtime.InteropServices;
using System.Threading;
using WSGM.Core;

namespace WSGM.Interop;

/// <summary>Registers the overlay global hotkey on the message window.</summary>
public sealed class HotkeyService : IDisposable
{
    // The MessageWindow is a process singleton, and two OverlayControllers (shell +
    // settings test) can each own a HotkeyService: a fixed id would make the second
    // RegisterHotKey fail and one WM_HOTKEY fire Pressed on both instances.
    private static int _nextId;

    // ERROR_HOTKEY_ALREADY_REGISTERED: Windows rejects a duplicate
    // hwnd+modifiers+key even under a different id, so the shell's own
    // registration is what blocks a second controller's — not a third-party app.
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly int _hotkeyId;
    private readonly MessageWindow _window;
    private bool _registered;

    /// <summary>Raised when Windows delivers the registered global shortcut.</summary>
    public event Action? Pressed;

    /// <summary>Creates a global-hotkey registration service for the process message window.</summary>
    /// <param name="window">The message-only window that receives <c>WM_HOTKEY</c>.</param>
    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _hotkeyId = Interlocked.Increment(ref _nextId);
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    private void OnHotkeyPressed(int id)
    {
        if (id == _hotkeyId)
        {
            Pressed?.Invoke();
        }
    }

    /// <summary>Replaces the registered shortcut with the supplied configuration.</summary>
    /// <param name="config">The enabled shortcut to register, or a disabled configuration to unregister.</param>
    public void Apply(HotkeyConfig config)
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, _hotkeyId);
            _registered = false;
        }
        if (!config.Enabled)
        {
            return;
        }

        uint modifiers = NativeMethods.ModNoRepeat;
        if (config.Ctrl)
        {
            modifiers |= NativeMethods.ModControl;
        }

        if (config.Alt)
        {
            modifiers |= NativeMethods.ModAlt;
        }

        if (config.Shift)
        {
            modifiers |= NativeMethods.ModShift;
        }

        if (config.Win)
        {
            modifiers |= NativeMethods.ModWin;
        }

        _registered = NativeMethods.RegisterHotKey(_window.Handle, _hotkeyId, modifiers, (uint)config.VirtualKey);
        if (_registered)
        {
            Log.Info($"Hotkey registered (id {_hotkeyId}, vk 0x{config.VirtualKey:X}, mods 0x{modifiers:X})");
        }
        else
        {
            var error = Marshal.GetLastWin32Error();
            Log.Warn(error == ErrorHotkeyAlreadyRegistered
                ? $"RegisterHotKey skipped (id {_hotkeyId}, vk 0x{config.VirtualKey:X}, mods 0x{modifiers:X}): the combination is already registered by this process, so this instance's global shortcut stays inert."
                : $"RegisterHotKey failed (id {_hotkeyId}, vk 0x{config.VirtualKey:X}, mods 0x{modifiers:X}, Win32 error {error}) — combination may be taken by another app.");
        }
    }

    /// <summary>Unregisters the current shortcut and detaches from the message window.</summary>
    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, _hotkeyId);
            _registered = false;
        }
        _window.HotkeyPressed -= OnHotkeyPressed;
    }
}
