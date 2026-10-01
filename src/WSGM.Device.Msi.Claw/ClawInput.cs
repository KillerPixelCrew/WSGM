using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Windows;

namespace WSGM.Device.Msi.Claw;

internal static class ClawControllerCodec
{
    public static CanonicalControllerSample Decode(
        ReadOnlySpan<byte> report,
        DateTimeOffset timestamp,
        OemButtonLatch? oemButtons = null)
    {
        if (report.Length != 64 || report[0] != 0x01)
        {
            throw new ArgumentException("The Claw DirectInput source requires a 64-byte report 0x01.", nameof(report));
        }

        var buttons = CanonicalButtons.None;
        buttons |= IsSet(report[5], 4) ? CanonicalButtons.X : 0;
        buttons |= IsSet(report[5], 5) ? CanonicalButtons.A : 0;
        buttons |= IsSet(report[5], 6) ? CanonicalButtons.B : 0;
        buttons |= IsSet(report[5], 7) ? CanonicalButtons.Y : 0;
        buttons |= IsSet(report[6], 0) ? CanonicalButtons.LeftShoulder : 0;
        buttons |= IsSet(report[6], 1) ? CanonicalButtons.RightShoulder : 0;
        buttons |= IsSet(report[6], 4) ? CanonicalButtons.View : 0;
        buttons |= IsSet(report[6], 5) ? CanonicalButtons.Menu : 0;
        buttons |= IsSet(report[6], 6) ? CanonicalButtons.LeftStick : 0;
        buttons |= IsSet(report[6], 7) ? CanonicalButtons.RightStick : 0;
        // Measured on MS-1T52: byte 7 bit 4 is the LEFT paddle (M1), and bit 3 is
        // the RIGHT paddle (M2). Handheld Companion has these two assignments reversed.
        buttons |= IsSet(report[7], 4) ? CanonicalButtons.RearPaddle1 : 0;
        buttons |= IsSet(report[7], 3) ? CanonicalButtons.RearPaddle2 : 0;
        buttons |= DecodeHat(report[5] & 0x0F);

        // The two front OEM buttons are not in this report (the firmware delivers them as WMI
        // events), so they are merged in from the latch that receives those events.
        buttons |= oemButtons?.Current(timestamp) ?? CanonicalButtons.None;

        return new CanonicalControllerSample
        {
            Timestamp = timestamp,
            Buttons = buttons,
            LeftStickX = Axis(report[1]),
            LeftStickY = -Axis(report[2]),
            RightStickX = Axis(report[3]),
            RightStickY = -Axis(report[4]),
            LeftTrigger = report[8] / 255f,
            RightTrigger = report[9] / 255f
        };
    }

    public static byte[] EncodeRumble(byte weak, byte strong, int reportLength = 11)
    {
        if (reportLength < 11)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reportLength),
                "The Claw rumble payload needs at least 11 bytes.");
        }

        var report = new byte[reportLength];
        WriteRumbleReport(report, weak, strong);
        return report;
    }

    public static void WriteRumbleReport(Span<byte> report, byte weak, byte strong)
    {
        if (report.Length < 11)
        {
            throw new ArgumentException("The Claw rumble payload needs at least 11 bytes.", nameof(report));
        }

        report.Clear();
        report[0] = 0x05;
        report[1] = 0x01;
        report[4] = weak;
        report[5] = strong;
    }

    private static bool IsSet(byte value, int bit)
    {
        return (value & (1 << bit)) != 0;
    }

    private static float Axis(byte value)
    {
        return Math.Clamp((value - 128) / 127f, -1, 1);
    }

    private static CanonicalButtons DecodeHat(int hat)
    {
        return hat switch
        {
            0 => CanonicalButtons.DPadUp,
            1 => CanonicalButtons.DPadUp | CanonicalButtons.DPadRight,
            2 => CanonicalButtons.DPadRight,
            3 => CanonicalButtons.DPadRight | CanonicalButtons.DPadDown,
            4 => CanonicalButtons.DPadDown,
            5 => CanonicalButtons.DPadDown | CanonicalButtons.DPadLeft,
            6 => CanonicalButtons.DPadLeft,
            7 => CanonicalButtons.DPadLeft | CanonicalButtons.DPadUp,
            _ => CanonicalButtons.None
        };
    }
}

/// <summary>The QS press a firmware chord stands for, per HC's <c>OEMChords</c>.</summary>
internal enum FirmwareChord
{
    None,

    /// <summary><c>LWin+G</c>, HC's "QS" chord.</summary>
    QuickSettings,

    /// <summary><c>LWin+Tab</c>, HC's "QS, Long-press" chord.</summary>
    QuickSettingsLong
}

internal readonly record struct ChordDecision(
    bool Suppress,
    bool ReleaseLeftWindows,
    bool ReleaseRightWindows,
    FirmwareChord Chord = FirmwareChord.None);

internal sealed class FirmwareChordStateMachine
{
    private bool _altDown;
    private bool _controlDown;
    private bool _gDown;
    private bool _leftWindowsDown;
    private bool _leftWindowsReleased;
    private bool _orphanAttempted;
    private bool _rightWindowsDown;
    private bool _rightWindowsReleased;
    private bool _shiftDown;
    private bool _tabDown;

    public ChordDecision Observe(uint virtualKey, bool keyDown, bool injected)
    {
        if (injected)
        {
            return default;
        }

        switch (virtualKey)
        {
            case NativeKeyboard.VK_LWIN:
                return ObserveWindows(true, keyDown);
            case NativeKeyboard.VK_RWIN:
                return ObserveWindows(false, keyDown);
            case NativeKeyboard.VK_CONTROL:
            case NativeKeyboard.VK_LCONTROL:
            case NativeKeyboard.VK_RCONTROL:
                _controlDown = keyDown;
                return default;
            case NativeKeyboard.VK_MENU:
            case NativeKeyboard.VK_LMENU:
            case NativeKeyboard.VK_RMENU:
                _altDown = keyDown;
                return default;
            case NativeKeyboard.VK_SHIFT:
            case NativeKeyboard.VK_LSHIFT:
            case NativeKeyboard.VK_RSHIFT:
                _shiftDown = keyDown;
                return default;
            case NativeKeyboard.VK_G:
                return ObserveTarget(ref _gDown, keyDown,
                    FirmwareChord.QuickSettings);
            case NativeKeyboard.VK_TAB:
                return ObserveTarget(ref _tabDown, keyDown,
                    FirmwareChord.QuickSettingsLong);
            default:
                return default;
        }
    }

    public void CommitSyntheticReleases(bool leftAccepted, bool rightAccepted)
    {
        _leftWindowsReleased |= leftAccepted;
        _rightWindowsReleased |= rightAccepted;
    }

    public void SynchronizeModifiers(bool controlDown, bool altDown, bool shiftDown)
    {
        _controlDown = controlDown;
        _altDown = altDown;
        _shiftDown = shiftDown;
    }

    public void Reset()
    {
        _orphanAttempted = false;
        _leftWindowsDown = false;
        _rightWindowsDown = false;
        _leftWindowsReleased = false;
        _rightWindowsReleased = false;
        _controlDown = false;
        _altDown = false;
        _shiftDown = false;
        _gDown = false;
        _tabDown = false;
    }

    public void InitializePreexisting(
        bool leftWindowsDown,
        bool rightWindowsDown,
        bool controlDown,
        bool altDown,
        bool shiftDown,
        bool gDown,
        bool tabDown)
    {
        Reset();
        _leftWindowsDown = leftWindowsDown;
        _rightWindowsDown = rightWindowsDown;
        _controlDown = controlDown;
        _altDown = altDown;
        _shiftDown = shiftDown;
        _gDown = gDown;
        _tabDown = tabDown;
    }

    private ChordDecision ObserveWindows(bool left, bool keyDown)
    {
        if (keyDown && !_leftWindowsDown && !_rightWindowsDown)
        {
            _orphanAttempted = false;
        }

        if (left)
        {
            if (!keyDown && _leftWindowsReleased)
            {
                _leftWindowsReleased = false;
                _leftWindowsDown = false;
                _orphanAttempted &= _rightWindowsDown;
                return new ChordDecision(true, false, false);
            }

            _leftWindowsDown = keyDown;
        }
        else
        {
            if (!keyDown && _rightWindowsReleased)
            {
                _rightWindowsReleased = false;
                _rightWindowsDown = false;
                _orphanAttempted &= _leftWindowsDown;
                return new ChordDecision(true, false, false);
            }

            _rightWindowsDown = keyDown;
        }

        if (!_leftWindowsDown && !_rightWindowsDown)
        {
            _orphanAttempted = false;
        }

        return default;
    }

    /// <summary>
    ///     The captured Claw burst omits G/Tab down: Win down, orphan target up, Win up.
    ///     A real keyboard supplies the target down, so its complete chord passes through.
    /// </summary>
    private ChordDecision ObserveTarget(ref bool targetDown, bool keyDown, FirmwareChord chord)
    {
        if (keyDown)
        {
            targetDown = true;
            return default;
        }

        var hadDown = targetDown;
        targetDown = false;
        if (hadDown
            || (!_leftWindowsDown && !_rightWindowsDown)
            || _controlDown
            || _altDown
            || _shiftDown)
        {
            return default;
        }

        if (_orphanAttempted)
        {
            return new ChordDecision(_leftWindowsReleased || _rightWindowsReleased, false, false);
        }

        _orphanAttempted = true;
        var releaseLeft = _leftWindowsDown && !_leftWindowsReleased;
        var releaseRight = _rightWindowsDown && !_rightWindowsReleased;
        return new ChordDecision(true, releaseLeft, releaseRight,
            releaseLeft || releaseRight ? chord : FirmwareChord.None);
    }
}

internal sealed class FirmwareChordSuppressor : IFirmwareChordSuppressor
{
    private const uint Marker = 0x5753474D;
    private static readonly int InputSize = Marshal.SizeOf<NativeKeyboard.Input>();
    private readonly NativeKeyboard.Input[] _batch = new NativeKeyboard.Input[4];
    private readonly NativeKeyboard.Input[] _cleanup = new NativeKeyboard.Input[1];
    private readonly KeyboardHookHandler _handler;
    private readonly LowLevelKeyboardHook _hook = new("WSGM Claw firmware chord suppressor");
    private readonly FirmwareChordStateMachine _state = new();
    private Action<FirmwareChord>? _chord;

    public FirmwareChordSuppressor()
    {
        _handler = Decide;
    }

    public ValueTask<bool> StartAsync(
        Action<Exception> fault,
        Action<FirmwareChord> chord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentNullException.ThrowIfNull(chord);
        _chord = chord;
        return _hook.StartAsync(_handler, fault, cancellationToken,
            () => _state.InitializePreexisting(
                LowLevelKeyboardHook.IsKeyDown(NativeKeyboard.VK_LWIN),
                LowLevelKeyboardHook.IsKeyDown(NativeKeyboard.VK_RWIN),
                LowLevelKeyboardHook.IsKeyDown(NativeKeyboard.VK_CONTROL),
                LowLevelKeyboardHook.IsKeyDown(NativeKeyboard.VK_MENU),
                LowLevelKeyboardHook.IsKeyDown(NativeKeyboard.VK_SHIFT),
                LowLevelKeyboardHook.IsKeyDown(NativeKeyboard.VK_G),
                LowLevelKeyboardHook.IsKeyDown(NativeKeyboard.VK_TAB)),
            _state.Reset);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        return _hook.StopAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _hook.DisposeAsync();
    }

    /// <summary>Decides one key event inside the hook: swallow the firmware's chords, pass everything else.</summary>
    private bool Decide(in KeyboardHookEvent key)
    {
        var injected = key.Injected || key.ExtraInfo == Marker;
        if (!injected && !key.Down && key.VirtualKey is NativeKeyboard.VK_G or NativeKeyboard.VK_TAB)
        {
            _state.SynchronizeModifiers(
                LowLevelKeyboardHook.IsKeyDown(NativeKeyboard.VK_CONTROL),
                LowLevelKeyboardHook.IsKeyDown(NativeKeyboard.VK_MENU),
                LowLevelKeyboardHook.IsKeyDown(NativeKeyboard.VK_SHIFT));
        }

        var decision = _state.Observe(key.VirtualKey, key.Down, injected);
        if (!decision.Suppress)
        {
            return false;
        }

        if (decision.Chord is not FirmwareChord.None)
        {
            // The receiver only queues the press; nothing here waits, allocates much or logs.
            _chord?.Invoke(decision.Chord);
        }

        if (decision is { ReleaseLeftWindows: false, ReleaseRightWindows: false })
        {
            return true;
        }

        var count = 0;
        _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_DUMMY, false, Marker);
        _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_DUMMY, true, Marker);
        var leftIndex = -1;
        var rightIndex = -1;
        if (decision.ReleaseLeftWindows)
        {
            leftIndex = count;
            _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_LWIN, true, Marker);
        }

        if (decision.ReleaseRightWindows)
        {
            rightIndex = count;
            _batch[count++] = NativeKeyboard.KeyInput(NativeKeyboard.VK_RWIN, true, Marker);
        }

        var sent = NativeKeyboard.SendInput(checked((uint)count), _batch, InputSize);
        if (sent == 1)
        {
            _cleanup[0] = NativeKeyboard.KeyInput(NativeKeyboard.VK_DUMMY, true, Marker);
            _ = NativeKeyboard.SendInput(1, _cleanup, InputSize);
        }

        var leftReleased = leftIndex >= 0 && sent > leftIndex;
        var rightReleased = rightIndex >= 0 && sent > rightIndex;
        _state.CommitSyntheticReleases(leftReleased, rightReleased);
        return leftReleased || rightReleased;
    }
}

internal static partial class NativeKeyboard
{
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint INPUT_KEYBOARD = 1;
    public const uint VK_TAB = 0x09;
    public const uint VK_SHIFT = 0x10;
    public const uint VK_CONTROL = 0x11;
    public const uint VK_MENU = 0x12;
    public const uint VK_LSHIFT = 0xA0;
    public const uint VK_RSHIFT = 0xA1;
    public const uint VK_LCONTROL = 0xA2;
    public const uint VK_RCONTROL = 0xA3;
    public const uint VK_LMENU = 0xA4;
    public const uint VK_RMENU = 0xA5;
    public const uint VK_LWIN = 0x5B;
    public const uint VK_RWIN = 0x5C;
    public const uint VK_G = 0x47;
    public const uint VK_DUMMY = 0xFF;

    public static Input KeyInput(uint virtualKey, bool keyUp, nuint extraInfo)
    {
        return new Input
        {
            Type = INPUT_KEYBOARD,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = checked((ushort)virtualKey),
                    Flags = (keyUp ? KEYEVENTF_KEYUP : 0)
                            | (virtualKey is VK_LWIN or VK_RWIN ? KEYEVENTF_EXTENDEDKEY : 0),
                    ExtraInfo = extraInfo
                }
            }
        };
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    // INPUT reserves the full mouse-sized union even for keyboard events: 32 bytes on x64.
    // A keyboard-only union makes cbSize 32 instead of 40, so SendInput rejects every release.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }
}
