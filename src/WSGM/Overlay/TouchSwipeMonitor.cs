using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>A screen edge from which WSGM recognizes an inward swipe.</summary>
internal enum ScreenEdge
{
    /// <summary>The bottom edge of the primary display.</summary>
    Bottom,

    /// <summary>The right edge of the primary display.</summary>
    Right,

    /// <summary>The left edge of the primary display.</summary>
    Left,

    /// <summary>The top edge of the primary display.</summary>
    Top
}

/// <summary>
///     Turns inward swipes from enabled screen edges into <see cref="Triggered" />
///     events by observing the touch digitizer through Raw Input (WM_INPUT on a
///     message-only window, RIDEV_INPUTSINK).
///     Purely observational: touch-screen input is registered without suppressing
///     legacy delivery. Nothing is consumed, and no window takes part in
///     hit-testing — the foreground game keeps receiving every event untouched.
///     Contact coordinates are parsed straight from the raw HID reports and scaled
///     from the digitizer's logical range to primary-screen physical pixels (the
///     built-in panel is assumed to be the primary display, as before).
///     Fallback knowledge if raw HID parsing ever fails on a device: a hit-testable
///     strip window (layered alpha 1, NOT 0 — fully transparent layered windows are
///     click-through) whose WM_NCHITTEST returns HTCLIENT only when
///     GetMessageExtraInfo() carries MI_WP_SIGNATURE ((extra &amp; 0xFFFFFF00) ==
///     0xFF515700, i.e. touch/pen-synthesized) and HTTRANSPARENT for real mouse.
/// </summary>
internal sealed unsafe class TouchSwipeMonitor : IDisposable
{
    private const string WindowClassName = "WSGM.RawTouchWindow";

    // Revised from the 2026-09-26 Claw traces, where 8 of 9 deliberate top swipes were rejected.
    // The start zone follows Windows' 2 mm first-report tolerance for an edge swipe; HHD's 2 %
    // start zone is the fallback when the digitizer reports no physical size. Direction is decided
    // once, from net displacement at the entry slop, the way Android's back gesture and GNOME's edge
    // drag do, so report-to-report jitter never accumulates against a straight swipe.
    private const double StartBandMm = 2.0;
    private const double StartBandFraction = 0.02;
    private const int MinStartBandPx = 8;
    private const int MaxStartBandPx = 48;
    private const int DiagnosticBandPx = 64;
    private const int EntrySlopPx = 16;
    private const ulong EntryWindowMs = 400;
    private const int TriggerDistancePx = 48;
    private const ulong TriggerTimeMs = 800;
    private const int DiagnosticLimitPerMinute = 12;

    private static readonly Lock Gate = new();

    // Raw-input registration is per-process per HID usage: registering a second
    // window RETARGETS delivery, and one RIDEV_REMOVE kills it for everyone. So
    // ONE shared message-only window owns the registration, WM_INPUT is dispatched
    // to every live monitor in this registry, and the registration is dropped only
    // when the last monitor is disposed (the Settings test overlay's monitor must
    // never take the live shell's edge swipes down with it).
    private static readonly List<TouchSwipeMonitor> Instances = [];

    // Replaced under Gate on every add and remove, so the window procedure reads the monitors
    // without taking the lock or copying the list for each WM_INPUT.
    private static TouchSwipeMonitor[] _instanceSnapshot = [];
    private static nint _sharedHwnd;

    private readonly Dictionary<nint, DeviceCaps> _devices = [];
    private bool _armed = true;
    private bool _bottomEnabled;
    private bool _contactWasDown;
    private int _diagnosticCount;
    private bool _diagnosticPending;
    private ulong _diagnosticWindowAt;
    private int _dispatchPending;
    private bool _disposed;
    private int _horizontalBandPx;
    private byte[] _inputBuffer = new byte[256];
    private bool _leftEnabled;
    private bool _loggedFirstReport;
    private bool _rightEnabled;
    private int _screenH;
    private int _screenW;
    private uint _startRawX;
    private uint _startRawY;
    private long _startedAt;
    private bool _topEnabled;
    private GestureTrace _trace;
    private bool _tracking;
    private ushort[] _usageBuffer = new ushort[16];
    private int _verticalBandPx;

    /// <summary>Creates a monitor and joins the shared process-wide raw-input registration.</summary>
    public TouchSwipeMonitor()
    {
        lock (Gate)
        {
            if (Instances.Count == 0)
            {
                CreateSharedWindowAndRegister();
            }

            Instances.Add(this);
            Volatile.Write(ref _instanceSnapshot, [.. Instances]);
        }
    }

    /// <summary>Stops monitoring and removes shared raw-input registration when last disposed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (Gate)
        {
            Instances.Remove(this);
            Volatile.Write(ref _instanceSnapshot, [.. Instances]);
            // The registration is process-wide: it may only go away with the LAST
            // monitor, or disposing the Settings test monitor would kill the live
            // shell's edge swipes until the shell restarts.
            if (Instances.Count == 0)
            {
                var devices = new[]
                {
                    new NativeMethods.RawInputDevice
                    {
                        usUsagePage = NativeMethods.HidUsagePageDigitizer,
                        usUsage = NativeMethods.HidUsageTouchScreen,
                        dwFlags = NativeMethods.RidevRemove,
                        hwndTarget = 0
                    }
                };
                if (!NativeMethods.RegisterRawInputDevices(devices, (uint)devices.Length,
                        (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>()))
                {
                    Log.Warn(
                        $"Raw touch input de-registration failed (Win32 error {Marshal.GetLastWin32Error()}); last touch monitor disposed.");
                }
                else
                {
                    Log.Info("Raw touch input unregistered (last touch monitor disposed).");
                }

                if (_sharedHwnd != 0)
                {
                    // DestroyWindow fails from a thread other than the one that
                    // created the window; the window then still exists, so the
                    // handle must not be cleared as if it were gone.
                    if (NativeMethods.DestroyWindow(_sharedHwnd))
                    {
                        _sharedHwnd = 0;
                    }
                    else
                    {
                        Log.Warn(
                            $"Failed to destroy the raw touch input window (Win32 error {Marshal.GetLastWin32Error()}); the handle survives this teardown.");
                    }
                }
            }
        }

        foreach (var caps in _devices.Values.Where(device => device.PreparsedData != 0))
        {
            Marshal.FreeHGlobal(caps.PreparsedData);
        }

        _devices.Clear();
    }

    /// <summary>Raised on the Avalonia UI thread with the edge that was swiped.</summary>
    public event Action<ScreenEdge>? Triggered;

    private static void CreateSharedWindowAndRegister()
    {
        // Class registration + HWND_MESSAGE creation share MessageWindow's code
        // path; the raw-input registration below stays entirely local so its
        // semantics (dedicated INPUTSINK target, last-monitor teardown) are
        // unchanged.
        _sharedHwnd = MessageWindow.CreateMessageOnlyWindow(
            WindowClassName, &WndProc, "Failed to create raw touch input window.");

        var devices = new[]
        {
            new NativeMethods.RawInputDevice
            {
                usUsagePage = NativeMethods.HidUsagePageDigitizer,
                usUsage = NativeMethods.HidUsageTouchScreen,
                dwFlags = NativeMethods.RidevInputSink | NativeMethods.RidevDevNotify,
                hwndTarget = _sharedHwnd
            }
        };
        if (!NativeMethods.RegisterRawInputDevices(devices, (uint)devices.Length,
                (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>()))
        {
            Log.Warn($"Raw touch input registration failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        else
        {
            Log.Info($"Raw touch input registered (HID digitizer sink, foreground {DescribeForeground()}).");
        }
    }

    /// <summary>Applies the enabled-edge settings.</summary>
    /// <param name="gestures">The persisted gesture configuration to observe.</param>
    public void Configure(GestureConfig gestures)
    {
        _bottomEnabled = gestures.BottomEdge;
        _rightEnabled = gestures.RightEdgeSteamQuickAccess;
        _leftEnabled = gestures.LeftEdgeSteamMenu;
        _topEnabled = gestures.TopEdge;
        _tracking = false;
        _diagnosticPending = false;
        // Re-applied on every config reload, which the shell does often, so this restated an
        // unchanged gesture set 1,162 times in one session.
        Log.Change(
            "touch.edges",
            $"Touch edge swipes configured (bottom={_bottomEnabled}, top={_topEnabled}, " +
            $"left-steam={_leftEnabled}, right-qam={_rightEnabled}, start-band={StartBandMm}mm, " +
            $"entry={EntrySlopPx}px/{EntryWindowMs}ms net 1:1, travel={TriggerDistancePx}px/{TriggerTimeMs}ms net 2:1, " +
            "second contact cancels).");
    }

    /// <summary>Resume gesture detection (overlay closed).</summary>
    public void Arm()
    {
        if (_disposed || _armed)
        {
            return;
        }

        _armed = true;
        // Reset the one-shot so every arm cycle proves whether raw reports
        // still flow — swipes reportedly die when specific apps take focus.
        _loggedFirstReport = false;
        Log.Info($"Touch edge swipes armed (foreground {DescribeForeground()}).");
    }

    private static string DescribeForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
        {
            return "none";
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            return $"0x{hwnd:X} ({Process.GetProcessById((int)pid).ProcessName})";
        }
        catch
        {
            return $"0x{hwnd:X}";
        }
    }

    /// <summary>Suspend gesture detection (overlay open).</summary>
    public void Disarm()
    {
        if (!_armed)
        {
            return;
        }

        _armed = false;
        _tracking = false;
        _diagnosticPending = false;
        Log.Info("Touch edge swipes disarmed.");
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (hwnd != _sharedHwnd)
        {
            return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
        }

        var monitors = Volatile.Read(ref _instanceSnapshot);
        try
        {
            switch (message)
            {
                case NativeMethods.WmInput:
                {
                    // hRawInput (lParam) is only valid during synchronous processing;
                    // read here, then still let DefWindowProc do the WM_INPUT cleanup.
                    foreach (var monitor in monitors)
                    {
                        if (!monitor._disposed)
                        {
                            monitor.ProcessRawInput(lParam);
                        }
                    }

                    break;
                }
                case NativeMethods.WmInputDeviceChange:
                    // With RIDEV_DEVNOTIFY, GIDC_ARRIVAL also fires at registration
                    // for devices already present — proves the WM_INPUT channel is
                    // alive before the first touch.
                    switch (wParam)
                    {
                        case NativeMethods.GidcArrival:
                            Log.Info($"Touch digitizer 0x{lParam:X} present.");
                            break;
                        case NativeMethods.GidcRemoval:
                        {
                            foreach (var monitor in monitors)
                            {
                                if (!monitor._disposed)
                                {
                                    monitor.EvictDevice(lParam);
                                }
                            }

                            break;
                        }
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Raw touch input processing failed", ex);
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void ProcessRawInput(nint hRawInput)
    {
        var headerSize = (uint)sizeof(NativeMethods.RawInputHeader);
        // Read straight into the reusable buffer. Once it has grown to the largest message seen, every
        // WM_INPUT costs one call instead of a size query followed by the read.
        fixed (byte* buffer = _inputBuffer)
        {
            var capacity = (uint)_inputBuffer.Length;
            var read = NativeMethods.GetRawInputData(hRawInput, NativeMethods.RidInput, (nint)buffer, ref capacity,
                headerSize);
            if (read != unchecked((uint)-1))
            {
                if (read >= headerSize)
                {
                    ProcessRawInputBuffer(buffer, read, headerSize);
                }

                return;
            }
        }

        // The buffer was too small for this message: size it, then read again.
        uint size = 0;
        if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RidInput, 0, ref size, headerSize) != 0 ||
            size < headerSize)
        {
            return;
        }

        if (_inputBuffer.Length < size)
        {
            _inputBuffer = new byte[size];
        }

        fixed (byte* buffer = _inputBuffer)
        {
            if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RidInput, (nint)buffer, ref size, headerSize) ==
                unchecked((uint)-1))
            {
                return;
            }

            ProcessRawInputBuffer(buffer, size, headerSize);
        }
    }

    private void ProcessRawInputBuffer(byte* buffer, uint size, uint headerSize)
    {
        {
            var header = *(NativeMethods.RawInputHeader*)buffer;
            if (header.dwType != NativeMethods.RimTypeHid)
            {
                return;
            }

            // Before any parsing, so the log separates "WM_INPUT never arrives"
            // from "reports arrive but don't parse". Re-logged once per arm
            // cycle to show whether delivery survives foreground changes.
            if (!_loggedFirstReport)
            {
                _loggedFirstReport = true;
                Log.Info($"Raw touch reports arriving (foreground {DescribeForeground()}).");
            }

            var caps = GetDeviceCaps(header.hDevice);
            if (caps is null)
            {
                return;
            }

            // The RAWHID prefix (dwSizeHid, dwCount) must fit before it is read,
            // and the report area is bounds-checked in 64-bit so inconsistent
            // dwSizeHid*dwCount cannot wrap the 32-bit multiply past the buffer.
            if (size < headerSize + 8)
            {
                return;
            }

            var hid = buffer + sizeof(NativeMethods.RawInputHeader);
            var reportSize = *(uint*)hid;
            var reportCount = *(uint*)(hid + 4);
            var reports = hid + 8;
            if (reportSize == 0 || (ulong)reportSize * reportCount > size - headerSize - 8)
            {
                return;
            }

            for (uint i = 0; i < reportCount; i++)
            {
                ProcessReport(caps, (nint)(reports + i * reportSize), reportSize);
            }
        }
    }

    private DeviceCaps? GetDeviceCaps(nint hDevice)
    {
        if (_devices.TryGetValue(hDevice, out var cached))
        {
            return cached.Usable ? cached : null;
        }

        var caps = BuildDeviceCaps(hDevice);
        _devices[hDevice] = caps;
        return caps.Usable ? caps : null;
    }

    private static DeviceCaps BuildDeviceCaps(nint hDevice)
    {
        var caps = new DeviceCaps();

        uint ppSize = 0;
        NativeMethods.GetRawInputDeviceInfoW(hDevice, NativeMethods.RidiPreparsedData, 0, ref ppSize);
        if (ppSize == 0)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: no preparsed HID data.");
            return caps;
        }

        var preparsed = Marshal.AllocHGlobal((int)ppSize);
        if (NativeMethods.GetRawInputDeviceInfoW(hDevice, NativeMethods.RidiPreparsedData, preparsed, ref ppSize) ==
            unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(preparsed);
            Log.Warn($"Touch digitizer 0x{hDevice:X}: could not read preparsed HID data.");
            return caps;
        }

        caps.PreparsedData = preparsed;

        if (NativeMethods.HidP_GetCaps(preparsed, out var hidCaps) != NativeMethods.HidpStatusSuccess ||
            hidCaps.UsagePage != NativeMethods.HidUsagePageDigitizer ||
            hidCaps.Usage != NativeMethods.HidUsageTouchScreen)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: not a touch-screen collection, ignoring.");
            return caps;
        }

        // Every input usage/value owns one data index, so this bounds how many
        // button usages HidP_GetUsages can ever return for one report.
        caps.UsageListLength = Math.Max(16, (int)hidCaps.NumberInputDataIndices);

        var count = hidCaps.NumberInputValueCaps;
        if (count == 0)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: no input value caps.");
            return caps;
        }

        var valueCaps = new NativeMethods.HidpValueCaps[count];
        if (NativeMethods.HidP_GetValueCaps(NativeMethods.HidpInput, valueCaps, ref count, preparsed) !=
            NativeMethods.HidpStatusSuccess)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: HidP_GetValueCaps failed.");
            return caps;
        }

        // Per contact slot (link collection), the digitizer exposes X/Y on the
        // Generic Desktop page. The lowest collection with both is the primary
        // contact — all a one-finger edge swipe needs.
        var xByCollection = new Dictionary<ushort, NativeMethods.HidpValueCaps>();
        var yByCollection = new Dictionary<ushort, NativeMethods.HidpValueCaps>();
        for (var i = 0; i < count; i++)
        {
            var vc = valueCaps[i];
            if (vc.UsagePage == NativeMethods.HidUsagePageDigitizer && Covers(vc, NativeMethods.HidUsageContactCount))
            {
                caps.HasContactCount = true;
                caps.ContactCountCollection = vc.LinkCollection;
                continue;
            }

            if (vc.UsagePage != NativeMethods.HidUsagePageGenericDesktop)
            {
                continue;
            }

            if (Covers(vc, NativeMethods.HidUsageX))
            {
                xByCollection.TryAdd(vc.LinkCollection, vc);
            }

            if (Covers(vc, NativeMethods.HidUsageY))
            {
                yByCollection.TryAdd(vc.LinkCollection, vc);
            }
        }

        var found = false;
        ushort bestCollection = 0;
        foreach (var collection in xByCollection.Keys.Where(yByCollection.ContainsKey))
        {
            if (found && collection >= bestCollection)
            {
                continue;
            }

            bestCollection = collection;
            found = true;
        }

        if (!found)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: no link collection with both X and Y.");
            return caps;
        }

        var x = xByCollection[bestCollection];
        var y = yByCollection[bestCollection];
        if (x.LogicalMax <= x.LogicalMin || y.LogicalMax <= y.LogicalMin)
        {
            Log.Warn(
                $"Touch digitizer 0x{hDevice:X}: degenerate logical ranges X {x.LogicalMin}..{x.LogicalMax}, Y {y.LogicalMin}..{y.LogicalMax}.");
            return caps;
        }

        caps.LinkCollection = bestCollection;
        caps.XMin = x.LogicalMin;
        caps.XMax = x.LogicalMax;
        caps.YMin = y.LogicalMin;
        caps.YMax = y.LogicalMax;
        caps.XSpanMm = PhysicalSpanMm(x.Units, x.UnitsExp, x.PhysicalMin, x.PhysicalMax);
        caps.YSpanMm = PhysicalSpanMm(y.Units, y.UnitsExp, y.PhysicalMin, y.PhysicalMax);
        caps.Usable = true;
        Log.Info($"Touch digitizer 0x{hDevice:X}: link {bestCollection}, X {x.LogicalMin}..{x.LogicalMax}, " +
                 $"Y {y.LogicalMin}..{y.LogicalMax}, physical {caps.XSpanMm:0.#}x{caps.YSpanMm:0.#} mm " +
                 $"(units 0x{x.Units:X}/0x{x.UnitsExp:X}), contact count " +
                 (caps.HasContactCount ? $"link {caps.ContactCountCollection}." : "absent."));
        return caps;

        static bool Covers(NativeMethods.HidpValueCaps vc, ushort usage)
        {
            return vc.IsRange != 0 ? vc.UsageMin <= usage && vc.UsageMax >= usage : vc.UsageMin == usage;
        }
    }

    /// <summary>
    ///     Converts a HID axis's physical extent to millimetres, or returns 0 when the descriptor
    ///     gives no plausible linear size for a handheld or tablet panel.
    /// </summary>
    /// <param name="units">The HID Unit item: nibble 0 is the system, nibble 1 the length exponent.</param>
    /// <param name="unitsExp">The HID Unit Exponent item, a four-bit signed code.</param>
    /// <param name="physicalMin">The axis's Physical Minimum.</param>
    /// <param name="physicalMax">The axis's Physical Maximum.</param>
    /// <returns>The axis length in millimetres, or 0 when unknown.</returns>
    internal static double PhysicalSpanMm(uint units, uint unitsExp, int physicalMin, int physicalMax)
    {
        // Only plain length is usable: SI linear (centimetres) or English linear (inches), to the
        // first power, with no mass, time or other unit nibbles set.
        var unitMm = units switch
        {
            0x11 => 10.0,
            0x13 => 25.4,
            _ => 0.0
        };
        if (unitMm == 0 || physicalMax <= physicalMin)
        {
            return 0;
        }

        // Codes 0x0-0x7 are exponents 0..7 and 0x8-0xF are -8..-1. Some descriptors store the
        // exponent as a signed byte (0xFE for -2); its low nibble carries the same code.
        var exponent = (int)(unitsExp & 0xF);
        if (exponent >= 8)
        {
            exponent -= 16;
        }

        var span = (physicalMax - physicalMin) * unitMm * Math.Pow(10, exponent);
        return span is >= 30 and <= 600 ? span : 0;
    }

    /// <summary>Returns the first-contact zone width, in pixels, along one screen axis.</summary>
    /// <param name="axisSpanMm">The digitizer's physical length on this axis, or 0 when unknown.</param>
    /// <param name="screenPx">The screen's pixel length on the same axis.</param>
    /// <returns>A zone of <see cref="StartBandMm" />, or <see cref="StartBandFraction" /> of the axis without a size.</returns>
    internal static int StartBandPx(double axisSpanMm, int screenPx)
    {
        var band = axisSpanMm > 0 ? StartBandMm * screenPx / axisSpanMm : StartBandFraction * screenPx;
        return Math.Clamp((int)Math.Round(band), MinStartBandPx, MaxStartBandPx);
    }

    private void EvictDevice(nint hDevice)
    {
        if (_devices.Remove(hDevice, out var caps) && caps.PreparsedData != 0)
        {
            Marshal.FreeHGlobal(caps.PreparsedData);
        }
    }

    private void ProcessReport(DeviceCaps caps, nint report, uint reportLength)
    {
        uint contacts = 1;
        if (caps.HasContactCount &&
            NativeMethods.HidP_GetUsageValue(
                NativeMethods.HidpInput, NativeMethods.HidUsagePageDigitizer, caps.ContactCountCollection,
                NativeMethods.HidUsageContactCount, out contacts, caps.PreparsedData, report, reportLength) !=
            NativeMethods.HidpStatusSuccess)
        {
            contacts = 1;
        }

        // In hybrid mode the first report of a frame carries the total contact count and the rest
        // carry 0; their first slot holds a later contact, not the primary one, so a continuation
        // report must not read as the swiping finger moving or lifting.
        if (contacts == 0)
        {
            return;
        }

        var tipDown = false;
        if (_usageBuffer.Length < caps.UsageListLength)
        {
            _usageBuffer = new ushort[caps.UsageListLength];
        }

        var usageCount = (uint)_usageBuffer.Length;
        var status = NativeMethods.HidP_GetUsages(
            NativeMethods.HidpInput, NativeMethods.HidUsagePageDigitizer, caps.LinkCollection,
            _usageBuffer, ref usageCount, caps.PreparsedData, report, reportLength);
        if (status == NativeMethods.HidpStatusSuccess)
        {
            for (var i = 0; i < usageCount; i++)
            {
                if (_usageBuffer[i] != NativeMethods.HidUsageTipSwitch)
                {
                    continue;
                }

                tipDown = true;
                break;
            }
        }
        else if (!caps.WarnedUsagesFailed)
        {
            // Once per device: a failure here silently reads as contact-up, which
            // would otherwise look like "touch dead" in a pasted log.
            caps.WarnedUsagesFailed = true;
            Log.Warn(
                $"HidP_GetUsages failed (status 0x{status:X8}, buffer {_usageBuffer.Length}) — reports treated as contact-up.");
        }

        if (!tipDown)
        {
            CompleteDiagnostic(caps, "released");
            _contactWasDown = false;
            _tracking = false;
            return;
        }

        if (NativeMethods.HidP_GetUsageValue(
                NativeMethods.HidpInput, NativeMethods.HidUsagePageGenericDesktop, caps.LinkCollection,
                NativeMethods.HidUsageX, out var rawX, caps.PreparsedData, report, reportLength) !=
            NativeMethods.HidpStatusSuccess ||
            NativeMethods.HidP_GetUsageValue(
                NativeMethods.HidpInput, NativeMethods.HidUsagePageGenericDesktop, caps.LinkCollection,
                NativeMethods.HidUsageY, out var rawY, caps.PreparsedData, report, reportLength) !=
            NativeMethods.HidpStatusSuccess)
        {
            if (caps.WarnedBadReport)
            {
                return;
            }

            caps.WarnedBadReport = true;
            Log.Warn("Touch digitizer report without X/Y values, ignoring.");
            return;
        }

        if (!_contactWasDown)
        {
            _contactWasDown = true;
            OnContactDown(caps, rawX, rawY);
        }
        else if (contacts == 1)
        {
            OnContactMove(caps, rawX, rawY);
        }

        // An edge swipe is one finger, as in Android and HHD. A second contact ends it until every
        // finger lifts, so a gripping thumb cannot take over the primary slot mid-gesture.
        if (contacts > 1 && (_tracking || _diagnosticPending))
        {
            _trace.Cancel("second-contact");
            _tracking = false;
            CompleteDiagnostic(caps, "cancelled");
        }
    }

    private void OnContactDown(DeviceCaps caps, uint rawX, uint rawY)
    {
        _tracking = false;
        _diagnosticPending = false;
        if (!_armed)
        {
            return;
        }

        _screenW = NativeMethods.GetSystemMetrics(0);
        _screenH = NativeMethods.GetSystemMetrics(1);
        var (x, y) = ScaleToScreen(caps, rawX, rawY);
        _horizontalBandPx = StartBandPx(caps.XSpanMm, _screenW);
        _verticalBandPx = StartBandPx(caps.YSpanMm, _screenH);

        // TickCount64 advances in 15.6 ms steps, too coarse for the entry window.
        _startedAt = Stopwatch.GetTimestamp();
        _startRawX = rawX;
        _startRawY = rawY;
        _trace = new GestureTrace(x, y, _screenW, _screenH, _horizontalBandPx, _verticalBandPx,
            _bottomEnabled, _rightEnabled, _leftEnabled, _topEnabled);
        _tracking = _trace.HasCandidates;
        // Verbose calibration includes the old title-bar region so rejected starts are visible.
        // One summary per contact, capped before formatting, never one line per HID sample.
        _diagnosticPending = Log.MinimumLevel <= LogLevel.Debug &&
                             ((_bottomEnabled && y >= _screenH - DiagnosticBandPx) ||
                              (_rightEnabled && x >= _screenW - DiagnosticBandPx) ||
                              (_leftEnabled && x < DiagnosticBandPx) ||
                              (_topEnabled && y < DiagnosticBandPx));
    }

    private void OnContactMove(DeviceCaps caps, uint rawX, uint rawY)
    {
        if (!_tracking && !_diagnosticPending)
        {
            return;
        }

        if (!_armed)
        {
            _tracking = false;
            _diagnosticPending = false;
            return;
        }

        var (x, y) = ScaleToScreen(caps, rawX, rawY);
        var triggeredEdge = _trace.Move(x, y, ElapsedMs());
        _tracking = _trace.HasCandidates;
        if (triggeredEdge is null)
        {
            return;
        }

        _tracking = false;
        CompleteDiagnostic(caps, "triggered");
        if (Interlocked.Exchange(ref _dispatchPending, 1) != 0)
        {
            return;
        }

        var edge = triggeredEdge.Value;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _dispatchPending, 0);
            if (_disposed)
            {
                return;
            }

            Log.Info($"{edge} touch edge swipe triggered.");
            Triggered?.Invoke(edge);
        });
    }

    private void CompleteDiagnostic(DeviceCaps caps, string outcome)
    {
        if (!_diagnosticPending)
        {
            return;
        }

        _diagnosticPending = false;
        var now = (ulong)Environment.TickCount64;
        if (now - _diagnosticWindowAt >= 60_000)
        {
            _diagnosticWindowAt = now;
            _diagnosticCount = 0;
        }

        if (_diagnosticCount >= DiagnosticLimitPerMinute || Log.MinimumLevel > LogLevel.Debug)
        {
            return;
        }

        _diagnosticCount++;
        Log.Debug($"Touch edge trace: {outcome}, decision={_trace.Decision}, " +
                  $"first-raw={_startRawX},{_startRawY}, ranges={caps.XMin}..{caps.XMax}/{caps.YMin}..{caps.YMax}, " +
                  $"first-px={_trace.StartX},{_trace.StartY}, last-px={_trace.LastX},{_trace.LastY}, " +
                  $"screen={_screenW}x{_screenH}, start-band-x/y={_horizontalBandPx}/{_verticalBandPx}px, " +
                  $"elapsed={ElapsedMs()}ms, dwell-2px={_trace.FirstMovementMs?.ToString() ?? "none"}ms, " +
                  $"entry-{EntrySlopPx}px={_trace.EntryMs?.ToString() ?? "none"}ms, " +
                  $"path-x/y={_trace.HorizontalTravel}/{_trace.VerticalTravel}px.");
    }

    private ulong ElapsedMs()
    {
        return (ulong)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
    }

    /// <summary>Calculates how far a contact has moved inward from its tracked edge.</summary>
    /// <param name="edge">The edge that started the gesture.</param>
    /// <param name="startX">Starting horizontal screen coordinate.</param>
    /// <param name="startY">Starting vertical screen coordinate.</param>
    /// <param name="x">Current horizontal screen coordinate.</param>
    /// <param name="y">Current vertical screen coordinate.</param>
    /// <returns>The signed inward distance in physical pixels.</returns>
    internal static int InwardDistance(ScreenEdge edge, int startX, int startY, int x, int y)
    {
        return edge switch
        {
            ScreenEdge.Bottom => startY - y,
            ScreenEdge.Right => startX - x,
            ScreenEdge.Left => x - startX,
            ScreenEdge.Top => y - startY,
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
    }

    /// <summary>Calculates the net movement along a tracked edge, parallel to it.</summary>
    /// <param name="edge">The edge that started the gesture.</param>
    /// <param name="startX">Starting horizontal screen coordinate.</param>
    /// <param name="startY">Starting vertical screen coordinate.</param>
    /// <param name="x">Current horizontal screen coordinate.</param>
    /// <param name="y">Current vertical screen coordinate.</param>
    /// <returns>The absolute sideways displacement in physical pixels.</returns>
    internal static int SidewaysDistance(ScreenEdge edge, int startX, int startY, int x, int y)
    {
        return edge is ScreenEdge.Top or ScreenEdge.Bottom ? Math.Abs(x - startX) : Math.Abs(y - startY);
    }

    /// <summary>
    ///     Selects the candidate edge whose inward movement has crossed the trigger distance by the
    ///     greatest amount and dominates net sideways displacement 2:1. Tracking all candidates makes
    ///     corner-origin gestures follow their movement instead of an arbitrary edge priority.
    /// </summary>
    /// <param name="bottomCandidate">Whether the contact began inside the bottom band.</param>
    /// <param name="rightCandidate">Whether the contact began inside the right band.</param>
    /// <param name="leftCandidate">Whether the contact began inside the left band.</param>
    /// <param name="topCandidate">Whether the contact began inside the top band.</param>
    /// <param name="startX">Starting horizontal screen coordinate.</param>
    /// <param name="startY">Starting vertical screen coordinate.</param>
    /// <param name="x">Current horizontal screen coordinate.</param>
    /// <param name="y">Current vertical screen coordinate.</param>
    /// <param name="triggerDistance">Required inward distance in physical pixels.</param>
    /// <returns>The movement-matching edge, or null while none has crossed the threshold.</returns>
    internal static ScreenEdge? PickTriggeredEdge(
        bool bottomCandidate, bool rightCandidate, bool leftCandidate, bool topCandidate,
        int startX, int startY, int x, int y, int triggerDistance)
    {
        ScreenEdge? bestEdge = null;
        var bestDistance = triggerDistance - 1;
        Consider(ScreenEdge.Bottom, bottomCandidate);
        Consider(ScreenEdge.Right, rightCandidate);
        Consider(ScreenEdge.Left, leftCandidate);
        Consider(ScreenEdge.Top, topCandidate);
        return bestEdge;

        void Consider(ScreenEdge edge, bool candidate)
        {
            if (!candidate)
            {
                return;
            }

            var distance = InwardDistance(edge, startX, startY, x, y);
            var sideways = SidewaysDistance(edge, startX, startY, x, y);
            if (distance <= bestDistance || distance < sideways * 2)
            {
                return;
            }

            bestDistance = distance;
            bestEdge = edge;
        }
    }

    private (int X, int Y) ScaleToScreen(DeviceCaps caps, uint rawX, uint rawY)
    {
        var x = (int)((rawX - caps.XMin) * (_screenW - 1) / (caps.XMax - caps.XMin));
        var y = (int)((rawY - caps.YMin) * (_screenH - 1) / (caps.YMax - caps.YMin));
        return (x, y);
    }

    /// <summary>Allocation-free state for one contact, independent of raw-input and UI ownership.</summary>
    internal struct GestureTrace
    {
        private int _candidates;
        private int _entered;

        internal GestureTrace(int x, int y, int screenWidth, int screenHeight, int horizontalBand, int verticalBand,
            bool bottomEnabled, bool rightEnabled, bool leftEnabled, bool topEnabled)
        {
            this = default;
            StartX = LastX = x;
            StartY = LastY = y;
            if (x >= 0 && y >= 0 && x < screenWidth && y < screenHeight)
            {
                Add(ScreenEdge.Bottom, bottomEnabled && y >= screenHeight - verticalBand);
                Add(ScreenEdge.Right, rightEnabled && x >= screenWidth - horizontalBand);
                Add(ScreenEdge.Left, leftEnabled && x < horizontalBand);
                Add(ScreenEdge.Top, topEnabled && y < verticalBand);
            }

            Decision = HasCandidates ? "waiting" : "outside-start-band";
        }

        internal int StartX { get; }
        internal int StartY { get; }
        internal int LastX { get; private set; }
        internal int LastY { get; private set; }
        internal int HorizontalTravel { get; private set; }
        internal int VerticalTravel { get; private set; }
        internal ulong? FirstMovementMs { get; private set; }
        internal ulong? EntryMs { get; private set; }
        internal string Decision { get; private set; }
        internal readonly bool HasCandidates => _candidates != 0;

        private void Add(ScreenEdge edge, bool enabled)
        {
            if (enabled)
            {
                _candidates |= 1 << (int)edge;
            }
        }

        internal ScreenEdge? Move(int x, int y, ulong elapsedMs)
        {
            HorizontalTravel += Math.Abs(x - LastX);
            VerticalTravel += Math.Abs(y - LastY);
            LastX = x;
            LastY = y;
            if (FirstMovementMs is null && Math.Max(Math.Abs(x - StartX), Math.Abs(y - StartY)) >= 2)
            {
                FirstMovementMs = elapsedMs;
            }

            if (!HasCandidates)
            {
                return null;
            }

            if (elapsedMs > TriggerTimeMs)
            {
                _candidates = 0;
                Decision = "expired";
                return null;
            }

            CheckEntry(ScreenEdge.Bottom, elapsedMs);
            CheckEntry(ScreenEdge.Right, elapsedMs);
            CheckEntry(ScreenEdge.Left, elapsedMs);
            CheckEntry(ScreenEdge.Top, elapsedMs);
            var admitted = _candidates & _entered;
            var edge = PickTriggeredEdge(
                (admitted & (1 << (int)ScreenEdge.Bottom)) != 0,
                (admitted & (1 << (int)ScreenEdge.Right)) != 0,
                (admitted & (1 << (int)ScreenEdge.Left)) != 0,
                (admitted & (1 << (int)ScreenEdge.Top)) != 0,
                StartX, StartY, x, y, TriggerDistancePx);
            if (edge is not null)
            {
                Decision = "accepted";
                _candidates = 0;
            }

            return edge;
        }

        /// <summary>Ends the gesture without a trigger, recording why.</summary>
        /// <param name="reason">The decision the diagnostic trace reports.</param>
        internal void Cancel(string reason)
        {
            _candidates = 0;
            Decision = reason;
        }

        private void CheckEntry(ScreenEdge edge, ulong elapsedMs)
        {
            var mask = 1 << (int)edge;
            if ((_candidates & mask) == 0 || (_entered & mask) != 0)
            {
                return;
            }

            // Direction is judged once, when the net displacement first leaves the slop. Until then
            // a finger resting on the bezel, or drifting while its edge coordinate is clamped, has
            // decided nothing; afterwards only the 2:1 trigger test applies.
            var inward = InwardDistance(edge, StartX, StartY, LastX, LastY);
            var sideways = SidewaysDistance(edge, StartX, StartY, LastX, LastY);
            if (Math.Max(Math.Abs(inward), sideways) < EntrySlopPx && elapsedMs <= EntryWindowMs)
            {
                return;
            }

            if (elapsedMs > EntryWindowMs)
            {
                _candidates &= ~mask;
                Decision = "late-entry";
            }
            else if (inward > sideways)
            {
                _entered |= mask;
                EntryMs = elapsedMs;
            }
            else
            {
                _candidates &= ~mask;
                Decision = "sideways-travel";
            }
        }
    }

    private sealed class DeviceCaps
    {
        public ushort ContactCountCollection;
        public bool HasContactCount;
        public ushort LinkCollection;
        public nint PreparsedData;
        public bool Usable;

        /// <summary>
        ///     Usage-list capacity for HidP_GetUsages, from HidP_GetCaps
        ///     (NumberInputDataIndices bounds the usages one input report can carry).
        /// </summary>
        public int UsageListLength = 16;

        public bool WarnedBadReport;
        public bool WarnedUsagesFailed;
        public int XMax;
        public int XMin;
        public double XSpanMm;
        public int YMax;
        public int YMin;
        public double YSpanMm;
    }
}
