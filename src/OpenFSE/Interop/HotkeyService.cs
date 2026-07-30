using System;
using OpenFSE.Core;

namespace OpenFSE.Interop;

/// <summary>Registers the overlay global hotkey on the message window.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 1;
    private readonly MessageWindow _window;
    private readonly Action<int> _hotkeyPressedHandler;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _hotkeyPressedHandler = id =>
        {
            if (id == HotkeyId)
            {
                Pressed?.Invoke();
            }
        };
        _window.HotkeyPressed += _hotkeyPressedHandler;
    }

    public void Apply(HotkeyConfig config)
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
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

        _registered = NativeMethods.RegisterHotKey(_window.Handle, HotkeyId, modifiers, (uint)config.VirtualKey);
        if (_registered)
        {
            Log.Info($"Hotkey registered (vk 0x{config.VirtualKey:X}, mods 0x{modifiers:X})");
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
            NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
        }
        _window.HotkeyPressed -= _hotkeyPressedHandler;
    }
}
