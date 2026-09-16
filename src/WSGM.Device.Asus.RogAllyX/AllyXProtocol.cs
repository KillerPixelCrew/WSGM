// SPDX-License-Identifier: MIT

using System;

namespace WSGM.Device.Asus.RogAllyX;

internal enum AllyVendorEvent
{
    CommandCenterClick,
    ArmouryClick,
    ArmouryHold,
    ArmouryHoldRelease,
    AlternateMenuClick
}

internal enum AllyRgbMode : byte
{
    Solid,
    Breathing,
    Rainbow,
    Spiral
}

internal enum AllyRgbSpeed
{
    Slow,
    Medium,
    Fast
}

internal readonly record struct AllyRgbColor(byte Red, byte Green, byte Blue);

/// <summary>Reference-derived bytes only; no transport, timing policy, or claim of live support.</summary>
internal static class AllyXProtocol
{
    internal const ushort VendorId = 0x0B05;
    internal const ushort ProductId = 0x1B4C;
    internal const ushort VendorUsagePage = 0xFF31;
    internal const ushort VendorUsage = 0x0080;
    private const int VendorReportLength = 64;
    private const int RumbleReportLength = 9;

    internal static bool TryReadVendorEvent(ReadOnlySpan<byte> report, out AllyVendorEvent value)
    {
        value = default;
        if (report.Length is < 2 or > VendorReportLength || report[0] != 0x5A)
        {
            return false;
        }

        AllyVendorEvent? decoded = report[1] switch
        {
            0xA6 => AllyVendorEvent.CommandCenterClick,
            0x38 => AllyVendorEvent.ArmouryClick,
            0xA7 => AllyVendorEvent.ArmouryHold,
            0xA8 => AllyVendorEvent.ArmouryHoldRelease,
            0x93 => AllyVendorEvent.AlternateMenuClick,
            _ => null
        };
        if (decoded is not { } known)
        {
            return false;
        }

        value = known;
        return true;
    }

    internal static bool TryWriteRumble(Span<byte> destination, float weak, float strong)
    {
        if (destination.Length < RumbleReportLength || !float.IsFinite(weak) || !float.IsFinite(strong))
        {
            return false;
        }

        var report = destination[..RumbleReportLength];
        report.Clear();
        report[0] = 0x0D;
        report[1] = 0x0F;
        report[4] = (byte)(Math.Clamp(weak, 0f, 1f) * 100);
        report[5] = (byte)(Math.Clamp(strong, 0f, 1f) * 100);
        report[6] = 0xFF;
        report[8] = 0xEB;
        return true;
    }

    internal static bool TryWriteColor(
        Span<byte> destination,
        byte zone,
        AllyRgbMode mode,
        AllyRgbSpeed speed,
        AllyRgbColor primary,
        AllyRgbColor secondary,
        bool reverse)
    {
        if (destination.Length < VendorReportLength || zone > 4 || !Enum.IsDefined(mode) || !Enum.IsDefined(speed))
        {
            return false;
        }

        var report = destination[..VendorReportLength];
        report.Clear();
        report[0] = 0x5A;
        report[1] = 0xB3;
        report[2] = zone;
        report[3] = (byte)mode;
        if (mode != AllyRgbMode.Spiral)
        {
            report[4] = primary.Red;
            report[5] = primary.Green;
            report[6] = primary.Blue;
        }

        report[7] = mode == AllyRgbMode.Solid ? (byte)0 : SpeedCode(speed);
        report[8] = mode == AllyRgbMode.Spiral && reverse ? (byte)1 : (byte)0;
        // ReSharper disable once InvertIf
        if (mode == AllyRgbMode.Breathing)
        {
            report[10] = secondary.Red;
            report[11] = secondary.Green;
            report[12] = secondary.Blue;
        }

        return true;
    }

    internal static bool TryWriteBrightness(Span<byte> destination, byte level)
    {
        if (destination.Length < VendorReportLength || level > 3)
        {
            return false;
        }

        destination[..VendorReportLength].Clear();
        destination[0] = 0x5A;
        destination[1] = 0xBA;
        destination[2] = 0xC5;
        destination[3] = 0xC4;
        destination[4] = level;
        return true;
    }

    internal static bool TryWriteRearKeyboardMapping(Span<byte> destination)
    {
        if (destination.Length < VendorReportLength)
        {
            return false;
        }

        destination[..VendorReportLength].Clear();
        destination[0] = 0x5A;
        destination[1] = 0xD1;
        destination[2] = 2;
        destination[3] = 8;
        destination[4] = 44;
        // Four eleven-byte mapping blocks: primary/secondary for each rear button.
        // These are ASUS mapping codes, not Windows VK codes or standard HID keyboard usages.
        destination[5] = 2;
        destination[7] = 0x28;
        destination[27] = 2;
        destination[29] = 0x30;
        return true;
    }

    internal static bool TryWriteMcuVersionRequest(Span<byte> destination)
    {
        if (destination.Length < VendorReportLength)
        {
            return false;
        }

        destination[..VendorReportLength].Clear();
        ReadOnlySpan<byte> request = [0x5A, 5, 3, 0x31, 0, 0x20];
        request.CopyTo(destination);
        return true;
    }

    internal static bool TryReadMcuVersion(ReadOnlySpan<byte> response, out int version)
    {
        version = 0;
        if (response.Length is < 7 or > VendorReportLength)
        {
            return false;
        }

        var dots = 0;
        for (var i = 0; i < response.Length; i++)
        {
            if (response[i] == 0)
            {
                return false;
            }

            if (response[i] != '.' || ++dots != 2)
            {
                continue;
            }

            if (i + 3 >= response.Length)
            {
                return false;
            }

            for (var j = 1; j <= 3; j++)
            {
                var digit = response[i + j];
                if (digit is < (byte)'0' or > (byte)'9')
                {
                    version = 0;
                    return false;
                }

                version = version * 10 + digit - '0';
            }

            // ReSharper disable once InvertIf
            if (i + 4 < response.Length && response[i + 4] is >= (byte)'0' and <= (byte)'9')
            {
                version = 0;
                return false;
            }

            return true;
        }

        return false;
    }

    private static byte SpeedCode(AllyRgbSpeed speed)
    {
        return speed switch
        {
            AllyRgbSpeed.Slow => 0xE1,
            AllyRgbSpeed.Medium => 0xEB,
            AllyRgbSpeed.Fast => 0xF5,
            _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null)
        };
    }
}
