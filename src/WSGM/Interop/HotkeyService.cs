using System;
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
    private readonly int _hotkeyId;
    private readonly MessageWindow _window;
    private bool _registered;

    public event Action? Pressed;

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
        if (config.Ctrl) modifiers |= NativeMethods.ModControl;
        if (config.Alt) modifiers |= NativeMethods.ModAlt;
        if (config.Shift) modifiers |= NativeMethods.ModShift;
        if (config.Win) modifiers |= NativeMethods.ModWin;

        _registered = NativeMethods.RegisterHotKey(_window.Handle, _hotkeyId, modifiers, (uint)config.VirtualKey);
        if (_registered)
        {
            Log.Info($"Hotkey registered (id {_hotkeyId}, vk 0x{config.VirtualKey:X}, mods 0x{modifiers:X})");
        }
        else
        {
            Log.Warn("RegisterHotKey failed — combination may be taken by another app.");
        }
    }

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
