using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace WSGM.Interop;

/// <summary>
///     The one synthetic keyboard sender: Steam's Big Picture shortcuts and the Overlay hotkeys both go through
///     <see cref="SendChord(IReadOnlyList{uint}, int)" />.
/// </summary>
/// <remarks>
///     Mirrors Handheld Companion 1.3.1.6 (<c>KeyboardCommands</c> over GregsStack InputSimulatorStandard's
///     <c>InputBuilder</c>): one <c>INPUT_KEYBOARD</c> per key event, carrying the virtual key and the scan code
///     <c>MapVirtualKey(vk, MAPVK_VK_TO_VSC) &amp; 0xFF</c>, with <c>KEYEVENTF_EXTENDEDKEY</c> for the extended list
///     and never <c>KEYEVENTF_SCANCODE</c>. Every key goes down in recorded order, then the press delay passes, then
///     every key comes up in the same order. The scan code is not optional: Steam's Big Picture resolves keys through
///     it, so a record with <c>wScan = 0</c> is accepted by <c>SendInput</c> and then does nothing.
/// </remarks>
internal static class KeyboardInput
{
    /// <summary>
    ///     Numpad Enter as Handheld Companion stores it (<c>VirtualKeyCode.NUMPAD_RETURN</c>): Enter's virtual key in
    ///     the low word, sent with the extended flag.
    /// </summary>
    internal const uint NumpadEnter = 0x4000000D;

    /// <summary>The longest press delay Handheld Companion accepts, in milliseconds.</summary>
    internal const int MaximumPressDelayMs = 5000;

    // InputBuilder.ExtendedKeys: NUMPAD_RETURN, MENU, RMENU, CONTROL, RCONTROL, INSERT, DELETE, HOME, END, PRIOR,
    // NEXT, RIGHT, UP, LEFT, DOWN, NUMLOCK, CANCEL, SNAPSHOT and DIVIDE. The left Win key is not in it.
    private static readonly HashSet<uint> ExtendedKeys =
    [
        NumpadEnter, 0x12, 0xA5, 0x11, 0xA3, 0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22, 0x27, 0x26, 0x25, 0x28, 0x90,
        0x03, 0x2C, 0x6F
    ];

    /// <summary>Sends Ctrl plus one key, leaving out Ctrl when the user already holds it.</summary>
    /// <param name="virtualKey">The key pressed with Ctrl.</param>
    /// <returns>How many of the key events Windows accepted.</returns>
    internal static SendResult SendControlChord(ushort virtualKey)
    {
        var controlAlreadyDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VkControl) &
                                  NativeMethods.KeyDownState) != 0;
        uint[] keys = controlAlreadyDown ? [virtualKey] : [NativeMethods.VkControl, virtualKey];
        return SendChord(keys, 0);
    }

    /// <summary>Presses a chord in the foreground window through Windows.</summary>
    /// <param name="keys">The keys in press order; <see cref="NumpadEnter" /> names Numpad Enter.</param>
    /// <param name="pressDelayMs">How long every key stays down, clamped to 0 to 5000 ms; blocks the caller that long.</param>
    /// <returns>How many of the key events Windows accepted, and the Win32 error of a partial send.</returns>
    internal static SendResult SendChord(IReadOnlyList<uint> keys, int pressDelayMs)
    {
        return SendChord(keys, pressDelayMs, NativePort.Instance);
    }

    /// <summary>Presses a chord through a port; tests record what it is handed.</summary>
    /// <param name="keys">The keys in press order.</param>
    /// <param name="pressDelayMs">How long every key stays down, clamped to 0 to 5000 ms.</param>
    /// <param name="port">Where the INPUT arrays, the scan-code lookups and the delay go.</param>
    /// <returns>How many of the key events were accepted.</returns>
    internal static SendResult SendChord(IReadOnlyList<uint> keys, int pressDelayMs, IInputPort port)
    {
        var downs = new NativeMethods.InputRecord[keys.Count];
        var ups = new NativeMethods.InputRecord[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            downs[index] = Record(keys[index], false, port);
            ups[index] = Record(keys[index], true, port);
        }

        var requested = (uint)(downs.Length + ups.Length);
        if (downs.Length == 0)
        {
            return new SendResult(0, 0, 0);
        }

        var sentDown = port.Send(downs);
        if (sentDown != downs.Length)
        {
            var error = port.LastError;
            // Never leave one of our synthetic keys down after a partial SendInput.
            if (sentDown > 0)
            {
                port.Send(ups[..(int)sentDown]);
            }

            return new SendResult(sentDown, requested, error);
        }

        var delay = Math.Clamp(pressDelayMs, 0, MaximumPressDelayMs);
        if (delay > 0)
        {
            port.Wait(delay);
        }

        var sentUp = port.Send(ups);
        var upError = 0;
        if (sentUp != ups.Length)
        {
            upError = port.LastError;
            port.Send(ups[(int)sentUp..]);
        }

        return new SendResult(sentDown + sentUp, requested, upError);
    }

    /// <summary>Whether Windows treats a key as an extended key, as Handheld Companion's list does.</summary>
    /// <param name="key">A key as <see cref="SendChord(IReadOnlyList{uint}, int)" /> takes it.</param>
    /// <returns>True for the extended list.</returns>
    internal static bool IsExtended(uint key)
    {
        return ExtendedKeys.Contains(key);
    }

    private static NativeMethods.InputRecord Record(uint key, bool up, IInputPort port)
    {
        var virtualKey = (ushort)(key & 0xFFFF);
        var flags = IsExtended(key) ? NativeMethods.KeyEventExtendedKey : 0;
        if (up)
        {
            flags |= NativeMethods.KeyEventKeyUp;
        }

        return new NativeMethods.InputRecord
        {
            type = NativeMethods.InputKeyboard,
            data = new NativeMethods.InputUnion
            {
                keyboard = new NativeMethods.KeyboardInputData
                {
                    virtualKey = virtualKey,
                    scanCode = (ushort)(port.ScanCode(virtualKey) & 0xFF),
                    flags = flags
                }
            }
        };
    }

    /// <summary>What a chord send reports: events accepted, events asked for and the Win32 error of a partial send.</summary>
    internal readonly record struct SendResult(uint Sent, uint Requested, int Error)
    {
        /// <summary>Whether Windows accepted every key event.</summary>
        internal bool Complete => Requested > 0 && Sent == Requested;
    }

    /// <summary>The Windows calls a chord send makes, so tests can record them.</summary>
    internal interface IInputPort
    {
        /// <summary>The Win32 error of the last <see cref="Send" /> that was not accepted completely.</summary>
        int LastError { get; }

        /// <summary>Hands key events to <c>SendInput</c>.</summary>
        /// <param name="inputs">The events.</param>
        /// <returns>How many Windows accepted.</returns>
        uint Send(NativeMethods.InputRecord[] inputs);

        /// <summary><c>MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC)</c>.</summary>
        /// <param name="virtualKey">The virtual key.</param>
        /// <returns>The scan code, or 0 when the key has none.</returns>
        uint ScanCode(ushort virtualKey);

        /// <summary>Holds the keys down.</summary>
        /// <param name="milliseconds">How long.</param>
        void Wait(int milliseconds);
    }

    private sealed class NativePort : IInputPort
    {
        internal static readonly NativePort Instance = new();

        public int LastError { get; private set; }

        public uint Send(NativeMethods.InputRecord[] inputs)
        {
            var sent = NativeMethods.SendInput((uint)inputs.Length, inputs,
                Marshal.SizeOf<NativeMethods.InputRecord>());
            LastError = sent == inputs.Length ? 0 : Marshal.GetLastPInvokeError();
            return sent;
        }

        public uint ScanCode(ushort virtualKey)
        {
            return NativeMethods.MapVirtualKeyW(virtualKey, NativeMethods.MapVkToVsc);
        }

        public void Wait(int milliseconds)
        {
            Thread.Sleep(milliseconds);
        }
    }
}
