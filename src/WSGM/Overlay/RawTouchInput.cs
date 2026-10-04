using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Overlay;

/// <summary>One decoded touch report: the primary contact of a digitizer frame.</summary>
/// <param name="Digitizer">The digitizer that sent it, with its logical and physical ranges.</param>
/// <param name="Contacts">How many contacts the frame reports; at least 1.</param>
/// <param name="TipDown">Whether the primary contact touches the panel.</param>
/// <param name="RawX">The primary contact's raw X, or 0 when the tip is up.</param>
/// <param name="RawY">The primary contact's raw Y, or 0 when the tip is up.</param>
internal readonly record struct TouchContactReport(
    TouchDigitizer Digitizer,
    uint Contacts,
    bool TipDown,
    uint RawX,
    uint RawY);

/// <summary>
///     The process's raw touch-screen input: one message-only window holding the HID digitizer
///     registration, shared by every subscriber.
/// </summary>
/// <remarks>
///     Raw-input registration is per-process per HID usage: registering a second window RETARGETS
///     delivery, and one RIDEV_REMOVE kills it for everyone. So one window owns the registration,
///     each WM_INPUT is read once and handed to every subscription, and the registration is dropped
///     only when the last subscription is disposed (the Settings test overlay must never take the
///     live shell's edge swipes down with it). Everything here runs on the UI thread, which owns the
///     window, so the window procedure, the subscriber list and the digitizer cache need no lock, and
///     preparsed data is never freed while the procedure reads it.
/// </remarks>
internal static unsafe class RawTouchInput
{
    private const string WindowClassName = "WSGM.RawTouchWindow";

    private static readonly Dictionary<nint, TouchDigitizer> Devices = [];
    private static Subscription[] _subscribers = [];
    private static nint _hwnd;
    private static byte[] _inputBuffer = new byte[256];
    private static ushort[] _usageBuffer = new ushort[16];
    private static bool _loggedFirstReport;

    /// <summary>Starts delivering decoded touch reports to <paramref name="handler" /> on the UI thread.</summary>
    /// <param name="handler">Receives each report; it must not allocate or log per report.</param>
    /// <returns>The subscription; disposing it stops delivery, and the last one ends the registration.</returns>
    internal static IDisposable Subscribe(Action<TouchContactReport> handler)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_subscribers.Length == 0)
        {
            CreateWindowAndRegister();
        }

        Subscription subscription = new(handler);
        _subscribers = [.. _subscribers, subscription];
        return subscription;
    }

    /// <summary>Logs the next arriving report once, to show whether delivery survives a focus change.</summary>
    internal static void LogNextArrival()
    {
        _loggedFirstReport = false;
    }

    /// <summary>Describes the foreground window for the touch diagnostics.</summary>
    /// <returns>The window handle and its process name, or <c>none</c>.</returns>
    internal static string DescribeForeground()
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

    private static void Unsubscribe(Subscription subscription)
    {
        var index = Array.IndexOf(_subscribers, subscription);
        if (index < 0)
        {
            return;
        }

        _subscribers = [.. _subscribers.Where(other => other != subscription)];
        if (_subscribers.Length == 0)
        {
            Unregister();
        }
    }

    private static void CreateWindowAndRegister()
    {
        // Class registration + HWND_MESSAGE creation share MessageWindow's code
        // path; the raw-input registration below stays entirely local so its
        // semantics (dedicated INPUTSINK target, last-subscriber teardown) are
        // unchanged.
        _hwnd = MessageWindow.CreateMessageOnlyWindow(
            WindowClassName, &WndProc, "Failed to create raw touch input window.");

        var devices = new[]
        {
            new NativeMethods.RawInputDevice
            {
                usUsagePage = NativeMethods.HidUsagePageDigitizer,
                usUsage = NativeMethods.HidUsageTouchScreen,
                dwFlags = NativeMethods.RidevInputSink | NativeMethods.RidevDevNotify,
                hwndTarget = _hwnd
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

    private static void Unregister()
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

        if (_hwnd != 0)
        {
            if (NativeMethods.DestroyWindow(_hwnd))
            {
                _hwnd = 0;
            }
            else
            {
                Log.Warn(
                    $"Failed to destroy the raw touch input window (Win32 error {Marshal.GetLastWin32Error()}); the handle survives this teardown.");
            }
        }

        foreach (var digitizer in Devices.Values.Where(device => device.PreparsedData != 0))
        {
            Marshal.FreeHGlobal(digitizer.PreparsedData);
        }

        Devices.Clear();
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (hwnd != _hwnd)
        {
            return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
        }

        try
        {
            switch (message)
            {
                case NativeMethods.WmInput:
                    // hRawInput (lParam) is only valid during synchronous processing;
                    // read here, then still let DefWindowProc do the WM_INPUT cleanup.
                    ProcessRawInput(lParam);
                    break;
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
                            EvictDevice(lParam);
                            break;
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

    private static void ProcessRawInput(nint hRawInput)
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

    private static void ProcessRawInputBuffer(byte* buffer, uint size, uint headerSize)
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

        var digitizer = GetDigitizer(header.hDevice);
        if (digitizer is null)
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
            ProcessReport(digitizer, (nint)(reports + i * reportSize), reportSize);
        }
    }

    private static TouchDigitizer? GetDigitizer(nint hDevice)
    {
        if (Devices.TryGetValue(hDevice, out var cached))
        {
            return cached.Usable ? cached : null;
        }

        var digitizer = BuildDigitizer(hDevice);
        Devices[hDevice] = digitizer;
        return digitizer.Usable ? digitizer : null;
    }

    private static TouchDigitizer BuildDigitizer(nint hDevice)
    {
        var caps = new TouchDigitizer();

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
        caps.XSpanMm = EdgeSwipeRecognizer.PhysicalSpanMm(x.Units, x.UnitsExp, x.PhysicalMin, x.PhysicalMax);
        caps.YSpanMm = EdgeSwipeRecognizer.PhysicalSpanMm(y.Units, y.UnitsExp, y.PhysicalMin, y.PhysicalMax);
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

    private static void EvictDevice(nint hDevice)
    {
        if (Devices.Remove(hDevice, out var digitizer) && digitizer.PreparsedData != 0)
        {
            Marshal.FreeHGlobal(digitizer.PreparsedData);
        }
    }

    private static void ProcessReport(TouchDigitizer caps, nint report, uint reportLength)
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
            Deliver(new TouchContactReport(caps, contacts, false, 0, 0));
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

        Deliver(new TouchContactReport(caps, contacts, true, rawX, rawY));
    }

    private static void Deliver(TouchContactReport report)
    {
        foreach (var subscriber in _subscribers)
        {
            subscriber.Handler(report);
        }
    }

    /// <summary>One subscriber's delivery; disposing it ends delivery on the UI thread.</summary>
    private sealed class Subscription(Action<TouchContactReport> handler) : IDisposable
    {
        internal Action<TouchContactReport> Handler { get; } = handler;

        public void Dispose()
        {
            // The window, its registration and the preparsed data belong to the UI thread.
            if (Dispatcher.UIThread.CheckAccess())
            {
                Unsubscribe(this);
            }
            else
            {
                Dispatcher.UIThread.Post(() => Unsubscribe(this));
            }
        }
    }
}

/// <summary>A touch digitizer as raw input decodes it: its HID layout and its coordinate ranges.</summary>
internal sealed class TouchDigitizer
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
