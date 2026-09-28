using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using WSGM.Device.Sdk.Input;
using WSGM.Input;
using WSGM.Interop;

namespace DeckSpike;

/// <summary>
///     Presents a virtual Steam Deck controller to this machine's Steam through VIIPER, exactly as WSGM
///     does, and lets the operator drive the triggers, the digital trigger bits and the analogue scale by
///     hand while every feedback frame Steam sends comes back to the console.
/// </summary>
internal static class Program
{
    private const uint BusId = 1;
    private const string ListenAddress = "127.0.0.1:0";
    private const byte Byte8L2 = 0x02;
    private const byte Byte8R2 = 0x01;

    private static readonly object Gate = new();
    private static readonly byte[] Frame = new byte[SteamDeckNeptuneReport.Length];
    private static readonly ConcurrentDictionary<int, int> SeenFeedback = new();
    private static readonly ConcurrentQueue<string> Feedback = new();

    private static uint _deviceId;
    private static uint _fastHandle;
    private static long _sequence;
    private static float _left;
    private static float _right;
    private static CanonicalButtons _buttons;
    private static DigitalMode _digital = DigitalMode.Wsgm;
    private static float _threshold = 0.8f;
    private static int _scale = 32767;
    private static volatile bool _quit;

    private enum DigitalMode
    {
        /// <summary>Whatever WSGM's encoder writes: the bit rises past 80 percent of travel.</summary>
        Wsgm,

        /// <summary>The bit follows the operator's own threshold.</summary>
        Threshold,

        /// <summary>Both bits held on whatever the travel.</summary>
        On,

        /// <summary>Both bits held off whatever the travel.</summary>
        Off
    }

    private static int Main(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--scale=", StringComparison.Ordinal))
            {
                _scale = int.Parse(arg[8..], CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--threshold=", StringComparison.Ordinal))
            {
                _threshold = int.Parse(arg[12..], CultureInfo.InvariantCulture) / 100f;
                _digital = DigitalMode.Threshold;
            }
            else if (arg == "--bit=on")
            {
                _digital = DigitalMode.On;
            }
            else if (arg == "--bit=off")
            {
                _digital = DigitalMode.Off;
            }
            else
            {
                Console.WriteLine("usage: DeckSpike [--scale=N] [--threshold=PERCENT] [--bit=on|off]");
                return 2;
            }
        }

        Console.WriteLine("DeckSpike: a virtual Steam Deck controller for trigger experiments.");
        ExposeUsbipTool();
        if (!Start(out var failure))
        {
            Console.WriteLine(failure);
            return 1;
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _quit = true;
        };
        PrintHelp();
        PrintState();
        var pump = new Thread(PumpFeedback) { IsBackground = true, Name = "feedback-printer" };
        pump.Start();
        try
        {
            Loop();
        }
        finally
        {
            Stop();
        }

        return 0;
    }

    private static void Loop()
    {
        while (!_quit)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(15);
                continue;
            }

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    _quit = true;
                    break;
                case ConsoleKey.Q:
                    Adjust(ref _left, key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 0.01f : 0.05f);
                    break;
                case ConsoleKey.A:
                    Adjust(ref _left, key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -0.01f : -0.05f);
                    break;
                case ConsoleKey.W:
                    Adjust(ref _right, key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 0.01f : 0.05f);
                    break;
                case ConsoleKey.S:
                    Adjust(ref _right, key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -0.01f : -0.05f);
                    break;
                case ConsoleKey.E:
                    Set(ref _left, 1f);
                    break;
                case ConsoleKey.D:
                    Set(ref _right, 1f);
                    break;
                case ConsoleKey.R:
                    lock (Gate)
                    {
                        _left = 0f;
                        _right = 0f;
                        Submit();
                    }

                    PrintState();
                    break;
                case ConsoleKey.Z:
                    Ramp(left: true, key.Modifiers.HasFlag(ConsoleModifiers.Shift));
                    break;
                case ConsoleKey.X:
                    Ramp(left: false, key.Modifiers.HasFlag(ConsoleModifiers.Shift));
                    break;
                case ConsoleKey.B:
                    _digital = _digital switch
                    {
                        DigitalMode.Wsgm => DigitalMode.Threshold,
                        DigitalMode.Threshold => DigitalMode.On,
                        DigitalMode.On => DigitalMode.Off,
                        _ => DigitalMode.Wsgm
                    };
                    Resubmit();
                    break;
                case ConsoleKey.T:
                    if (Prompt("digital threshold, percent of travel", 0, 100, out var percent))
                    {
                        _threshold = percent / 100f;
                        _digital = DigitalMode.Threshold;
                    }

                    Resubmit();
                    break;
                case ConsoleKey.M:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                    {
                        if (Prompt("wire value for full travel", 1, 65535, out var scale))
                        {
                            _scale = scale;
                        }
                    }
                    else
                    {
                        _scale = _scale switch
                        {
                            32767 => 35424,
                            35424 => 40000,
                            40000 => 65535,
                            _ => 32767
                        };
                    }

                    Resubmit();
                    break;
                case ConsoleKey.Enter:
                    Tap(CanonicalButtons.A);
                    break;
                case ConsoleKey.Spacebar:
                    lock (Gate)
                    {
                        _buttons ^= CanonicalButtons.A;
                        Submit();
                    }

                    PrintState();
                    break;
                case ConsoleKey.F:
                    PrintFrame();
                    break;
                case ConsoleKey.P:
                    PrintState();
                    break;
                case ConsoleKey.H:
                    PrintHelp();
                    break;
            }
        }
    }

    private static void Adjust(ref float travel, float delta)
    {
        lock (Gate)
        {
            travel = Math.Clamp(travel + delta, 0f, 1f);
            Submit();
        }

        PrintState();
    }

    private static void Set(ref float travel, float value)
    {
        lock (Gate)
        {
            travel = value;
            Submit();
        }

        PrintState();
    }

    private static void Resubmit()
    {
        lock (Gate)
        {
            Submit();
        }

        PrintState();
    }

    /// <summary>Pulls one trigger from rest to full travel over two seconds and holds it; the shifted variant releases again.</summary>
    private static void Ramp(bool left, bool andRelease)
    {
        var worker = new Thread(() =>
        {
            const int steps = 100;
            for (var i = 1; i <= steps && !_quit; i++)
            {
                lock (Gate)
                {
                    if (left)
                    {
                        _left = i / (float)steps;
                    }
                    else
                    {
                        _right = i / (float)steps;
                    }

                    Submit();
                }

                Thread.Sleep(20);
            }

            PrintState();
            if (!andRelease)
            {
                return;
            }

            Thread.Sleep(400);
            lock (Gate)
            {
                if (left)
                {
                    _left = 0f;
                }
                else
                {
                    _right = 0f;
                }

                Submit();
            }

            PrintState();
        }) { IsBackground = true, Name = "ramp" };
        worker.Start();
    }

    private static void Tap(CanonicalButtons button)
    {
        lock (Gate)
        {
            _buttons |= button;
            Submit();
        }

        Thread.Sleep(150);
        lock (Gate)
        {
            _buttons &= ~button;
            Submit();
        }

        Console.WriteLine($"tapped {button}");
    }

    /// <summary>Encodes the current state the way WSGM does, applies the experiment overrides and submits.</summary>
    private static unsafe void Submit()
    {
        var sample = new CanonicalControllerSample
        {
            Sequence = ++_sequence,
            CycleGeneration = 1,
            Timestamp = DateTimeOffset.UtcNow,
            Buttons = _buttons,
            LeftTrigger = _left,
            RightTrigger = _right
        };
        SteamDeckNeptuneReport.Write(sample, Frame);

        if (_scale != 32767)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(Frame.AsSpan(44, 2), Wire(_left));
            BinaryPrimitives.WriteUInt16LittleEndian(Frame.AsSpan(46, 2), Wire(_right));
        }

        switch (_digital)
        {
            case DigitalMode.Threshold:
                SetBit(Byte8L2, _left >= _threshold);
                SetBit(Byte8R2, _right >= _threshold);
                break;
            case DigitalMode.On:
                SetBit(Byte8L2, true);
                SetBit(Byte8R2, true);
                break;
            case DigitalMode.Off:
                SetBit(Byte8L2, false);
                SetBit(Byte8R2, false);
                break;
        }

        int status;
        fixed (byte* data = Frame)
        {
            status = NativeViiper.DeviceSetInputFast(_fastHandle, data, Frame.Length);
        }

        if (status != NativeViiper.Ok)
        {
            Console.WriteLine($"submit refused: status={status}, {NativeViiper.TakeLastError()}");
        }
    }

    private static ushort Wire(float travel)
    {
        return (ushort)Math.Clamp(MathF.Round(travel * _scale), 0, 65535);
    }

    private static void SetBit(byte bit, bool on)
    {
        if (on)
        {
            Frame[8] |= bit;
        }
        else
        {
            Frame[8] &= (byte)~bit;
        }
    }

    private static bool Start(out string failure)
    {
        try
        {
            if (NativeViiper.Init(ListenAddress) != NativeViiper.Ok)
            {
                failure = "VIIPER could not start: " + NativeViiper.TakeLastError();
                return false;
            }

            if (NativeViiper.BusCreate(BusId) != NativeViiper.Ok)
            {
                failure = "VIIPER could not create bus 1: " + NativeViiper.TakeLastError();
                NativeViiper.Shutdown();
                return false;
            }

            if (NativeViiper.DeviceAdd(BusId, "steamdeck", out _deviceId) != NativeViiper.Ok)
            {
                failure = "VIIPER could not add the Steam Deck device: " + NativeViiper.TakeLastError();
                NativeViiper.Shutdown();
                return false;
            }

            if (NativeViiper.DeviceOpenFast(BusId, _deviceId, out _fastHandle) != NativeViiper.Ok)
            {
                failure = "VIIPER could not open the submission handle: " + NativeViiper.TakeLastError();
                Stop();
                return false;
            }

            lock (Gate)
            {
                Submit();
            }

            unsafe
            {
                if (NativeViiper.DeviceSetFeedbackCallback(BusId, _deviceId, &OnFeedback, null) != NativeViiper.Ok)
                {
                    Console.WriteLine("feedback callback refused; Steam's writes will not be shown: "
                                      + NativeViiper.TakeLastError());
                }
            }

            if (NativeViiper.DeviceAttach(BusId, _deviceId) != NativeViiper.Ok)
            {
                failure = "VIIPER could not attach the device (is usbip-win2 installed, and is this console elevated?): "
                          + NativeViiper.TakeLastError();
                Stop();
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            failure = "libviiper.dll is not beside DeckSpike.exe. Run eng\\build-viiper.ps1 first.";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            failure = "The libviiper.dll beside DeckSpike.exe is the wrong version.";
            return false;
        }

        Console.WriteLine($"Steam Deck controller attached as VIIPER device {BusId}:{_deviceId}. Open Steam's controller settings.");
        failure = string.Empty;
        return true;
    }

    private static void Stop()
    {
        if (_deviceId != 0)
        {
            var status = NativeViiper.DeviceRemove(BusId, _deviceId);
            Console.WriteLine(status == NativeViiper.Ok
                ? "device removed"
                : "device removal refused: " + NativeViiper.TakeLastError());
            _deviceId = 0;
        }

        NativeViiper.Shutdown();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnFeedback(uint busId, uint deviceId, byte* data, int length, void* userData)
    {
        if (data is null || length <= 0)
        {
            return;
        }

        var copy = new byte[length];
        Marshal.Copy((IntPtr)data, copy, 0, length);
        Feedback.Enqueue(Describe(copy));
    }

    private static void PumpFeedback()
    {
        while (!_quit)
        {
            while (Feedback.TryDequeue(out var line))
            {
                var shape = line.GetHashCode(StringComparison.Ordinal);
                var count = SeenFeedback.AddOrUpdate(shape, 1, (_, n) => n + 1);
                if (count == 1 || count is 10 or 100 or 1000)
                {
                    Console.WriteLine(count == 1 ? $"  <- {line}" : $"  <- {line} (x{count})");
                }
            }

            Thread.Sleep(20);
        }
    }

    /// <summary>One line per feedback frame: settings writes by name, haptics summarised, the rest as hex.</summary>
    private static string Describe(byte[] report)
    {
        var body = report.Length > 1 && report[0] == 0x00 ? report.AsSpan(1) : report.AsSpan();
        if (body.Length == 0)
        {
            return "empty frame";
        }

        switch (body[0])
        {
            case 0x87 when body.Length >= 2:
            {
                var payload = Math.Min(body[1], body.Length - 2);
                List<string> settings = [];
                for (var offset = 2; offset + 2 < 2 + payload; offset += 3)
                {
                    var value = body[offset + 1] | (body[offset + 2] << 8);
                    settings.Add($"{SettingName(body[offset])}={value}");
                }

                return "settings write 0x87: " + string.Join(", ", settings);
            }
            case 0x86:
                return "factory reset 0x86";
            case 0x88:
                return "clear settings 0x88";
            case 0x8E:
                return "load default settings 0x8E";
            case 0x81:
                return "clear digital mappings 0x81";
            case 0x85:
                return "set default mappings 0x85";
            case 0xEB when body.Length >= 6:
                return $"rumble 0xEB: left={body[2] | (body[3] << 8)}, right={body[4] | (body[5] << 8)}";
            case 0xEA:
                return "trackpad haptic 0xEA";
            case 0x8F:
                return "haptic pulse 0x8F";
            case 0xDC:
                return "haptic event 0xDC";
            case 0xE2:
                return "haptic gain 0xE2";
            default:
                return $"0x{body[0]:X2} len {body.Length}: {Convert.ToHexString(body[..Math.Min(body.Length, 24)])}";
        }
    }

    private static string SettingName(byte id)
    {
        return id switch
        {
            0x07 => "LeftTrackpadMode",
            0x08 => "RightTrackpadMode",
            0x09 => "LizardMode",
            0x18 => "SmoothAbsoluteMouse",
            0x30 => "ImuMode",
            0x34 => "LeftTrackpadClickPressure",
            0x35 => "RightTrackpadClickPressure",
            0x3E => "TriggerMode",
            0x44 => "TriggerThresholdPercent",
            0x46 => "HapticsEnabled",
            0x47 => "SteamWatchdogEnable",
            _ => $"0x{id:X2}"
        } + $"(0x{id:X2})";
    }

    private static void PrintState()
    {
        lock (Gate)
        {
            var l2 = (Frame[8] & Byte8L2) != 0 ? 1 : 0;
            var r2 = (Frame[8] & Byte8R2) != 0 ? 1 : 0;
            var lRaw = BinaryPrimitives.ReadUInt16LittleEndian(Frame.AsSpan(44, 2));
            var rRaw = BinaryPrimitives.ReadUInt16LittleEndian(Frame.AsSpan(46, 2));
            var mode = _digital switch
            {
                DigitalMode.Wsgm => "wsgm (bit past 80%)",
                DigitalMode.Threshold => $"threshold {_threshold * 100:0}%",
                DigitalMode.On => "forced on",
                _ => "forced off"
            };
            Console.WriteLine(
                $"LT {_left * 100,3:0}% raw {lRaw,5} L2={l2} | RT {_right * 100,3:0}% raw {rRaw,5} R2={r2} | bits: {mode} | full travel = {_scale} | A={((_buttons & CanonicalButtons.A) != 0 ? "down" : "up")}");
        }
    }

    private static void PrintFrame()
    {
        lock (Gate)
        {
            Console.WriteLine("bytes 8-14 " + Convert.ToHexString(Frame.AsSpan(8, 7)) + "  bytes 44-47 "
                              + Convert.ToHexString(Frame.AsSpan(44, 4)));
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
                          keys: q/a  left trigger +5/-5 % (Shift: 1 %)     w/s  right trigger +5/-5 %
                                e    left trigger to 100 %                  d    right trigger to 100 %
                                r    release both triggers                  z/x  ramp left/right 0->100 % over 2 s (Shift: and release)
                                b    cycle the digital bit: wsgm -> threshold -> forced on -> forced off
                                t    set the digital threshold in percent   m    cycle full-travel wire value 32767/35424/40000/65535 (Shift: enter one)
                                Enter tap A   Space hold/release A   f frame bytes   p state   h help   Esc quit
                          Steam's writes to the pad print as '<-' lines, once per distinct frame.
                          """);
    }

    private static bool Prompt(string what, int min, int max, out int value)
    {
        Console.Write($"{what} [{min}-{max}]: ");
        var text = Console.ReadLine();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
        {
            return true;
        }

        Console.WriteLine("kept the previous value");
        value = 0;
        return false;
    }

    /// <summary>Puts usbip-win2's folder on this process's PATH when usbip.exe is not already reachable, as WSGM does.</summary>
    private static void ExposeUsbipTool()
    {
        const string tool = "usbip.exe";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => File.Exists(Path.Combine(entry.Trim(), tool))))
        {
            return;
        }

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip");
        if (File.Exists(Path.Combine(folder, tool)))
        {
            Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + path);
            Console.WriteLine($"using {folder} for usbip.exe");
            return;
        }

        Console.WriteLine("usbip.exe was not found on PATH or under Program Files\\USBip; the attach will fail until usbip-win2 is installed.");
    }
}
