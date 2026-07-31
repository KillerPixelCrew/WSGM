using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Input;

/// <summary>Records a keyboard shortcut by listening to raw key events with a
/// low-level hook, so we capture actual virtual-key codes (what RegisterHotKey wants)
/// instead of guessing them from a UI key enum. The hook lives only while recording.</summary>
public sealed partial class KeyRecorder : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    private const int VkLShift = 0xA0, VkRShift = 0xA1;
    private const int VkLControl = 0xA2, VkRControl = 0xA3;
    private const int VkLMenu = 0xA4, VkRMenu = 0xA5;
    private const int VkLWin = 0x5B, VkRWin = 0x5C;
    private const int VkEscape = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, nint lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    private static KeyRecorder? _active;
    private nint _hook;

    /// <summary>Fires with (modifier flags for RegisterHotKey, virtual key code).
    /// Escape cancels and reports (0, 0).</summary>
    public event Action<uint, int>? Recorded;

    public void Start()
    {
        Stop();
        _active = this;
        unsafe
        {
            delegate* unmanaged<int, nint, nint, nint> callback = &HookProc;
            _hook = SetWindowsHookExW(WhKeyboardLl, (nint)callback, 0, 0);
        }
        if (_hook == 0)
        {
            Log.Warn("Could not install keyboard hook for recording.");
            Recorded?.Invoke(0, 0);
        }
    }

    public void Stop()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        var recorder = _active;
        if (recorder is null || nCode < 0)
        {
            return CallNextHookEx(0, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        if (message is not (WmKeyDown or WmSysKeyDown))
        {
            return CallNextHookEx(0, nCode, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        var vk = (int)info.vkCode;

        // Modifier alone isn't a shortcut — keep waiting for the real key.
        if (IsModifier(vk))
        {
            return CallNextHookEx(0, nCode, wParam, lParam);
        }

        uint modifiers = 0;
        if (IsDown(VkControl)) modifiers |= Interop.NativeMethods.ModControl;
        if (IsDown(VkMenu)) modifiers |= Interop.NativeMethods.ModAlt;
        if (IsDown(VkShift)) modifiers |= Interop.NativeMethods.ModShift;
        if (IsDown(VkLWin) || IsDown(VkRWin)) modifiers |= Interop.NativeMethods.ModWin;

        var cancelled = vk == VkEscape;
        Dispatcher.UIThread.Post(() =>
        {
            recorder.Stop();
            recorder.Recorded?.Invoke(cancelled ? 0u : modifiers, cancelled ? 0 : vk);
        });

        // Swallow the key so recording doesn't type into the UI behind it.
        return 1;
    }

    private static bool IsModifier(int vk) =>
        vk is VkShift or VkControl or VkMenu
            or VkLShift or VkRShift or VkLControl or VkRControl
            or VkLMenu or VkRMenu or VkLWin or VkRWin;

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Human-readable shortcut text, e.g. "Ctrl + Alt + Home".</summary>
    public static string Describe(HotkeyConfig hotkey)
    {
        if (!hotkey.Enabled || hotkey.VirtualKey == 0)
        {
            return "None";
        }
        var parts = new System.Collections.Generic.List<string>();
        if (hotkey.Ctrl) parts.Add("Ctrl");
        if (hotkey.Alt) parts.Add("Alt");
        if (hotkey.Shift) parts.Add("Shift");
        if (hotkey.Win) parts.Add("Win");
        parts.Add(KeyName(hotkey.VirtualKey));
        return string.Join(" + ", parts);
    }

    public static string KeyName(int vk) => vk switch
    {
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause",
        0x14 => "Caps Lock", 0x1B => "Esc", 0x20 => "Space",
        0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x2C => "Print Screen", 0x2D => "Insert", 0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),                 // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),                 // A-Z
        >= 0x60 and <= 0x69 => $"Numpad {vk - 0x60}",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",                       // F1-F24
        0xBA => ";", 0xBB => "+", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
        0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        _ => $"Key 0x{vk:X2}",
    };

    public void Dispose() => Stop();
}
