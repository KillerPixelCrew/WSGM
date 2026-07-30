using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenFSE.Core;

namespace OpenFSE.Input;

/// <summary>Reads a standard HID gamepad (DualShock 4, DualSense, Switch Pro, and any
/// other HID pad) using Windows' own HID parser instead of hardcoded report offsets:
/// HidP_GetUsages reports which button usages are pressed, whatever the device's
/// report layout happens to be.
///
/// This matters because a virtual DS4 / DualSense / Switch Pro — all of which Handheld
/// Companion can emulate — is NOT visible to XInput at all, so without this OpenFSE
/// would see no buttons whatsoever on those emulation modes.</summary>
public sealed partial class HidGamepad
{
    private const ushort UsagePageGeneric = 0x01;
    private const ushort UsageGamepad = 0x05;
    private const ushort UsageJoystick = 0x04;
    private const ushort UsagePageButton = 0x09;
    private const int HidpStatusSuccess = 0x00110000;

    private readonly nint _handle;
    private readonly nint _preparsed;
    private readonly int _reportLength;
    private readonly ushort _maxUsageCount;

    public ushort VendorId { get; }
    public ushort ProductId { get; }

    private HidGamepad(nint handle, nint preparsed, int reportLength, ushort maxUsageCount,
        ushort vendorId, ushort productId)
    {
        _handle = handle;
        _preparsed = preparsed;
        _reportLength = reportLength;
        _maxUsageCount = maxUsageCount;
        VendorId = vendorId;
        ProductId = productId;
    }

    /// <summary>Opens the first HID gamepad that isn't a Valve device (those use a
    /// vendor-specific format handled by SteamHidController).</summary>
    public static HidGamepad? Open(Func<ushort, ushort, bool> skip)
    {
        foreach (var (path, vid, pid) in HidDevices.Enumerate())
        {
            if (skip(vid, pid))
            {
                continue;
            }

            var handle = HidDevices.OpenRead(path);
            if (handle == -1)
            {
                continue;
            }
            if (!HidDevices.TryGetPreparsed(handle, out var preparsed, out var caps))
            {
                HidDevices.Close(handle);
                continue;
            }

            var isGamepad = caps.UsagePage == UsagePageGeneric &&
                            caps.Usage is UsageGamepad or UsageJoystick &&
                            caps.InputReportByteLength > 0;
            if (!isGamepad)
            {
                HidDevices.FreePreparsed(preparsed);
                HidDevices.Close(handle);
                continue;
            }

            var maxUsages = HidP_MaxUsageListLength(0 /* HidP_Input */, UsagePageButton, preparsed);
            Log.Info($"HID gamepad opened: {vid:X4}:{pid:X4}, {caps.InputReportByteLength}-byte reports, " +
                     $"up to {maxUsages} buttons.");
            return new HidGamepad(handle, preparsed, caps.InputReportByteLength,
                (ushort)Math.Max(1, maxUsages), vid, pid);
        }
        return null;
    }

    /// <summary>Blocks until the next input report, then returns the pressed buttons.
    /// Null means the device went away.</summary>
    public GamepadButtons? Read()
    {
        var buffer = new byte[_reportLength];
        if (!HidDevices.Read(_handle, buffer, out var read) || read == 0)
        {
            return null;
        }

        var usages = new ushort[_maxUsageCount];
        uint count = _maxUsageCount;
        var status = HidP_GetUsages(0 /* HidP_Input */, UsagePageButton, 0, usages, ref count,
            _preparsed, buffer, (uint)read);
        if (status != HidpStatusSuccess)
        {
            return 0;
        }

        GamepadButtons state = 0;
        for (var i = 0; i < count; i++)
        {
            state |= ButtonMap.FromHidUsage(usages[i], VendorId, ProductId);
        }
        return state;
    }

    public void Close()
    {
        HidDevices.FreePreparsed(_preparsed);
        HidDevices.Close(_handle);
    }

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, nint preparsedData,
        [In] byte[] report, uint reportLength);

    [LibraryImport("hid.dll")]
    private static partial int HidP_MaxUsageListLength(int reportType, ushort usagePage, nint preparsedData);
}

/// <summary>Maps HID button numbers to OpenFSE buttons. Standard HID pads report
/// buttons as usages 1..N in the layout their firmware defines; the common console
/// pads all follow the same order, so the labels below hold for them and any unknown
/// pad still binds correctly (it just shows a generic name).</summary>
internal static class ButtonMap
{
    public static GamepadButtons FromHidUsage(ushort usage, ushort vid, ushort pid)
    {
        // Sony DualShock 4 / DualSense report order.
        var sony = vid == 0x054C;
        if (sony)
        {
            return usage switch
            {
                1 => GamepadButtons.X,              // Square
                2 => GamepadButtons.A,              // Cross
                3 => GamepadButtons.B,              // Circle
                4 => GamepadButtons.Y,              // Triangle
                5 => GamepadButtons.LeftShoulder,
                6 => GamepadButtons.RightShoulder,
                7 => GamepadButtons.LeftTrigger,
                8 => GamepadButtons.RightTrigger,
                9 => GamepadButtons.Back,           // Share / Create
                10 => GamepadButtons.Start,         // Options
                11 => GamepadButtons.LeftThumb,
                12 => GamepadButtons.RightThumb,
                13 => GamepadButtons.Steam,         // PS button
                14 => GamepadButtons.QuickAccess,   // Touchpad click
                15 => GamepadButtons.L4,            // Mute (DualSense)
                _ => 0,
            };
        }

        // Nintendo Switch Pro report order.
        if (vid == 0x057E)
        {
            return usage switch
            {
                1 => GamepadButtons.B,
                2 => GamepadButtons.A,
                3 => GamepadButtons.Y,
                4 => GamepadButtons.X,
                5 => GamepadButtons.LeftShoulder,
                6 => GamepadButtons.RightShoulder,
                7 => GamepadButtons.LeftTrigger,
                8 => GamepadButtons.RightTrigger,
                9 => GamepadButtons.Back,           // Minus
                10 => GamepadButtons.Start,         // Plus
                11 => GamepadButtons.LeftThumb,
                12 => GamepadButtons.RightThumb,
                13 => GamepadButtons.Steam,         // Home
                14 => GamepadButtons.QuickAccess,   // Capture
                _ => 0,
            };
        }

        // Unknown pad: keep the first buttons usable with generic names.
        return usage switch
        {
            1 => GamepadButtons.A,
            2 => GamepadButtons.B,
            3 => GamepadButtons.X,
            4 => GamepadButtons.Y,
            5 => GamepadButtons.LeftShoulder,
            6 => GamepadButtons.RightShoulder,
            7 => GamepadButtons.LeftTrigger,
            8 => GamepadButtons.RightTrigger,
            9 => GamepadButtons.Back,
            10 => GamepadButtons.Start,
            11 => GamepadButtons.LeftThumb,
            12 => GamepadButtons.RightThumb,
            13 => GamepadButtons.Steam,
            14 => GamepadButtons.QuickAccess,
            _ => 0,
        };
    }
}
