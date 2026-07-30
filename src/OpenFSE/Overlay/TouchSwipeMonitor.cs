using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;
using OpenFSE.Core;
using OpenFSE.Interop;

namespace OpenFSE.Overlay;

public enum ScreenEdge
{
    Bottom,
    Right,
}

/// <summary>
/// Turns inward swipes from the bottom/right screen edge into <see cref="Triggered"/>
/// events by observing the touch digitizer through Raw Input (WM_INPUT on a
/// message-only window, RIDEV_INPUTSINK).
///
/// Purely observational: only the touch-screen HID device class is registered
/// (never mouse or keyboard), nothing is consumed, and no window takes part in
/// hit-testing — the foreground game keeps receiving every event untouched.
/// Contact coordinates are parsed straight from the raw HID reports and scaled
/// from the digitizer's logical range to primary-screen physical pixels (the
/// built-in panel is assumed to be the primary display, as before).
///
/// Fallback knowledge if raw HID parsing ever fails on a device: a hit-testable
/// strip window (layered alpha 1, NOT 0 — fully transparent layered windows are
/// click-through) whose WM_NCHITTEST returns HTCLIENT only when
/// GetMessageExtraInfo() carries MI_WP_SIGNATURE ((extra &amp; 0xFFFFFF00) ==
/// 0xFF515700, i.e. touch/pen-synthesized) and HTTRANSPARENT for real mouse.
/// </summary>
public sealed unsafe class TouchSwipeMonitor : IDisposable
{
    private const string WindowClassName = "OpenFSE.RawTouchWindow";
    private const int MinimumBandPx = 48;
    private const int TriggerDistancePx = 48;
    private const ulong TriggerTimeMs = 800;
    private const int ErrorClassAlreadyExists = 1410;

    private static readonly object Gate = new();
    private static TouchSwipeMonitor? _instance;
    private static bool _windowClassRegistered;

    private sealed class DeviceCaps
    {
        public nint PreparsedData;
        public ushort LinkCollection;
        public int XMin;
        public int XMax;
        public int YMin;
        public int YMax;
        public bool Usable;
        public bool WarnedBadReport;
    }

    private readonly Dictionary<nint, DeviceCaps> _devices = [];
    private readonly ushort[] _usageBuffer = new ushort[16];
    private byte[] _inputBuffer = new byte[256];

    private nint _hwnd;
    private bool _bottomEnabled;
    private bool _rightEnabled;
    private int _bandPx = MinimumBandPx;
    private bool _armed = true;
    private bool _contactWasDown;
    private bool _tracking;
    private ScreenEdge _edge;
    private int _startX;
    private int _startY;
    private ulong _startedAt;
    private int _screenW;
    private int _screenH;
    private int _dispatchPending;
    private bool _loggedFirstReport;
    private bool _disposed;

    /// <summary>Raised on the Avalonia UI thread with the edge that was swiped.</summary>
    public event Action<ScreenEdge>? Triggered;

    public TouchSwipeMonitor()
    {
        var hInstance = NativeMethods.GetModuleHandleW(0);
        EnsureWindowClass(hInstance);

        _hwnd = NativeMethods.CreateWindowExW(
            0, WindowClassName, null, 0,
            0, 0, 0, 0,
            NativeMethods.HwndMessage, 0, hInstance, 0);
        if (_hwnd == 0)
        {
            throw new InvalidOperationException("Failed to create raw touch input window.");
        }
        _instance = this;

        var devices = new[]
        {
            new NativeMethods.RawInputDevice
            {
                usUsagePage = NativeMethods.HidUsagePageDigitizer,
                usUsage = NativeMethods.HidUsageTouchScreen,
                dwFlags = NativeMethods.RidevInputSink | NativeMethods.RidevDevNotify,
                hwndTarget = _hwnd,
            },
        };
        if (!NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>()))
        {
            Log.Warn($"Raw touch input registration failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        else
        {
            Log.Info($"Raw touch input registered (HID digitizer sink, foreground {DescribeForeground()}).");
        }
    }

    public void Configure(GestureConfig gestures)
    {
        _bottomEnabled = gestures.BottomEdge;
        _rightEnabled = gestures.RightEdge;
        _bandPx = Math.Max(MinimumBandPx, gestures.StripThickness);
        _tracking = false;
        Log.Info($"Touch edge swipes configured (bottom={_bottomEnabled}, right={_rightEnabled}, band={_bandPx}px).");
    }

    /// <summary>Resume gesture detection (overlay closed).</summary>
    public void Arm()
    {
        if (!_disposed && !_armed)
        {
            _armed = true;
            // Reset the one-shot so every arm cycle proves whether raw reports
            // still flow — swipes reportedly die when specific apps take focus.
            _loggedFirstReport = false;
            Log.Info($"Touch edge swipes armed (foreground {DescribeForeground()}).");
        }
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
            return $"0x{hwnd:X} ({System.Diagnostics.Process.GetProcessById((int)pid).ProcessName})";
        }
        catch
        {
            return $"0x{hwnd:X}";
        }
    }

    /// <summary>Suspend gesture detection (overlay open).</summary>
    public void Disarm()
    {
        if (_armed)
        {
            _armed = false;
            _tracking = false;
            Log.Info("Touch edge swipes disarmed.");
        }
    }

    private static void EnsureWindowClass(nint hInstance)
    {
        lock (Gate)
        {
            if (_windowClassRegistered)
            {
                return;
            }

            var className = WindowClassName + "\0";
            fixed (char* classNamePointer = className)
            {
                var windowClass = new NativeMethods.WndClassW
                {
                    hInstance = hInstance,
                    lpszClassName = (nint)classNamePointer,
                    lpfnWndProc = &WndProc,
                };
                var atom = NativeMethods.RegisterClassW(&windowClass);
                if (atom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
                {
                    throw new InvalidOperationException("Failed to register raw touch input window class.");
                }
            }

            _windowClassRegistered = true;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        var monitor = _instance;
        if (monitor is not null && hwnd == monitor._hwnd && !monitor._disposed)
        {
            try
            {
                if (message == NativeMethods.WmInput)
                {
                    // hRawInput (lParam) is only valid during synchronous processing;
                    // read here, then still let DefWindowProc do the WM_INPUT cleanup.
                    monitor.ProcessRawInput(lParam);
                }
                else if (message == NativeMethods.WmInputDeviceChange)
                {
                    // With RIDEV_DEVNOTIFY, GIDC_ARRIVAL also fires at registration
                    // for devices already present — proves the WM_INPUT channel is
                    // alive before the first touch.
                    if (wParam == NativeMethods.GidcArrival)
                    {
                        Log.Info($"Touch digitizer 0x{lParam:X} present.");
                    }
                    else if (wParam == NativeMethods.GidcRemoval)
                    {
                        monitor.EvictDevice(lParam);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Raw touch input processing failed", ex);
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void ProcessRawInput(nint hRawInput)
    {
        var headerSize = (uint)sizeof(NativeMethods.RawInputHeader);
        uint size = 0;
        if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RidInput, 0, ref size, headerSize) != 0 ||
            size == 0)
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

            var hid = buffer + sizeof(NativeMethods.RawInputHeader);
            var reportSize = *(uint*)hid;
            var reportCount = *(uint*)(hid + 4);
            var reports = hid + 8;
            if (reportSize == 0 || (nint)(reports + reportSize * reportCount) > (nint)(buffer + size))
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

    private DeviceCaps BuildDeviceCaps(nint hDevice)
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
        var xByCollection = new Dictionary<ushort, (int Min, int Max)>();
        var yByCollection = new Dictionary<ushort, (int Min, int Max)>();
        for (var i = 0; i < count; i++)
        {
            var vc = valueCaps[i];
            if (vc.UsagePage != NativeMethods.HidUsagePageGenericDesktop)
            {
                continue;
            }
            var coversX = vc.IsRange != 0
                ? vc.UsageMin <= NativeMethods.HidUsageX && NativeMethods.HidUsageX <= vc.UsageMax
                : vc.UsageMin == NativeMethods.HidUsageX;
            var coversY = vc.IsRange != 0
                ? vc.UsageMin <= NativeMethods.HidUsageY && NativeMethods.HidUsageY <= vc.UsageMax
                : vc.UsageMin == NativeMethods.HidUsageY;
            if (coversX && !xByCollection.ContainsKey(vc.LinkCollection))
            {
                xByCollection[vc.LinkCollection] = (vc.LogicalMin, vc.LogicalMax);
            }
            if (coversY && !yByCollection.ContainsKey(vc.LinkCollection))
            {
                yByCollection[vc.LinkCollection] = (vc.LogicalMin, vc.LogicalMax);
            }
        }

        var found = false;
        ushort bestCollection = 0;
        foreach (var collection in xByCollection.Keys)
        {
            if (yByCollection.ContainsKey(collection) && (!found || collection < bestCollection))
            {
                bestCollection = collection;
                found = true;
            }
        }
        if (!found)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: no link collection with both X and Y.");
            return caps;
        }

        var x = xByCollection[bestCollection];
        var y = yByCollection[bestCollection];
        if (x.Max <= x.Min || y.Max <= y.Min)
        {
            Log.Warn($"Touch digitizer 0x{hDevice:X}: degenerate logical ranges X {x.Min}..{x.Max}, Y {y.Min}..{y.Max}.");
            return caps;
        }

        caps.LinkCollection = bestCollection;
        caps.XMin = x.Min;
        caps.XMax = x.Max;
        caps.YMin = y.Min;
        caps.YMax = y.Max;
        caps.Usable = true;
        Log.Info($"Touch digitizer 0x{hDevice:X}: link {bestCollection}, X {x.Min}..{x.Max}, Y {y.Min}..{y.Max}.");
        return caps;
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
        var tipDown = false;
        var usageCount = (uint)_usageBuffer.Length;
        var status = NativeMethods.HidP_GetUsages(
            NativeMethods.HidpInput, NativeMethods.HidUsagePageDigitizer, caps.LinkCollection,
            _usageBuffer, ref usageCount, caps.PreparsedData, report, reportLength);
        if (status == NativeMethods.HidpStatusSuccess)
        {
            for (var i = 0; i < usageCount; i++)
            {
                if (_usageBuffer[i] == NativeMethods.HidUsageTipSwitch)
                {
                    tipDown = true;
                    break;
                }
            }
        }

        if (!tipDown)
        {
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
            if (!caps.WarnedBadReport)
            {
                caps.WarnedBadReport = true;
                Log.Warn("Touch digitizer report without X/Y values, ignoring.");
            }
            return;
        }

        if (!_contactWasDown)
        {
            _contactWasDown = true;
            OnContactDown(caps, rawX, rawY);
            return;
        }

        OnContactMove(caps, rawX, rawY);
    }

    private void OnContactDown(DeviceCaps caps, uint rawX, uint rawY)
    {
        _tracking = false;
        if (!_armed)
        {
            return;
        }

        _screenW = NativeMethods.GetSystemMetrics(0);
        _screenH = NativeMethods.GetSystemMetrics(1);
        var (x, y) = ScaleToScreen(caps, rawX, rawY);

        if (_bottomEnabled && y >= _screenH - _bandPx)
        {
            _edge = ScreenEdge.Bottom;
        }
        else if (_rightEnabled && x >= _screenW - _bandPx)
        {
            _edge = ScreenEdge.Right;
        }
        else
        {
            return;
        }

        _tracking = true;
        _startX = x;
        _startY = y;
        _startedAt = (ulong)Environment.TickCount64;
        Log.Info($"{_edge} touch edge swipe started at {x},{y}.");
    }

    private void OnContactMove(DeviceCaps caps, uint rawX, uint rawY)
    {
        if (!_tracking)
        {
            return;
        }
        if (!_armed)
        {
            _tracking = false;
            return;
        }
        if ((ulong)Environment.TickCount64 - _startedAt > TriggerTimeMs)
        {
            _tracking = false;
            return;
        }

        var (x, y) = ScaleToScreen(caps, rawX, rawY);
        var inwardDistance = _edge == ScreenEdge.Bottom ? _startY - y : _startX - x;
        if (inwardDistance < TriggerDistancePx)
        {
            return;
        }

        _tracking = false;
        if (Interlocked.Exchange(ref _dispatchPending, 1) != 0)
        {
            return;
        }

        var edge = _edge;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _dispatchPending, 0);
            if (_disposed)
            {
                return;
            }
            Log.Info($"{edge} touch edge swipe triggered quick access.");
            Triggered?.Invoke(edge);
        });
    }

    private (int X, int Y) ScaleToScreen(DeviceCaps caps, uint rawX, uint rawY)
    {
        var x = (int)(((long)rawX - caps.XMin) * (_screenW - 1) / (caps.XMax - caps.XMin));
        var y = (int)(((long)rawY - caps.YMin) * (_screenH - 1) / (caps.YMax - caps.YMin));
        return (x, y);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        var devices = new[]
        {
            new NativeMethods.RawInputDevice
            {
                usUsagePage = NativeMethods.HidUsagePageDigitizer,
                usUsage = NativeMethods.HidUsageTouchScreen,
                dwFlags = NativeMethods.RidevRemove,
                hwndTarget = 0,
            },
        };
        NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RawInputDevice>());

        if (_hwnd != 0)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
        foreach (var caps in _devices.Values)
        {
            if (caps.PreparsedData != 0)
            {
                Marshal.FreeHGlobal(caps.PreparsedData);
            }
        }
        _devices.Clear();
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }
}
