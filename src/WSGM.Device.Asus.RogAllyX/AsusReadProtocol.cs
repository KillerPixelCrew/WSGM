// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace WSGM.Device.Asus.RogAllyX;

internal enum AsusReadControl : uint
{
    FirmwareProfile = 0x00120075,
    SustainedPower = 0x001200A3,
    SlowPower = 0x001200A0,
    FastPower = 0x001200C1,
    ChargeLimit = 0x00120057,
    CpuFanSpeed = 0x00110013,
    GpuFanSpeed = 0x00110014,
    CpuFactoryFanCurve = 0x00110024,
    GpuFactoryFanCurve = 0x00110025
}

internal readonly record struct AsusScalar(uint Raw, ushort Value, uint Flags);

internal sealed record FactoryFanCurve(IReadOnlyList<byte> Temperatures, IReadOnlyList<byte> DutyPercent);

/// <summary>Reference-derived DSTS status query encoding; nothing here writes device state.</summary>
internal static class AsusReadProtocol
{
    internal const uint Ioctl = 0x0022240C;
    private const uint DeviceStatusMethod = 0x53545344;
    internal const int QueryLength = 16;
    internal const int ResponseLength = 32;

    internal static bool IsCurve(AsusReadControl control)
    {
        return control is AsusReadControl.CpuFactoryFanCurve or AsusReadControl.GpuFactoryFanCurve;
    }

    internal static bool TryWriteQuery(Span<byte> destination, AsusReadControl control, int profile = 0)
    {
        if (destination.Length < QueryLength
            || !Enum.IsDefined(control)
            || (IsCurve(control) ? profile is < 0 or > 2 : profile != 0))
        {
            return false;
        }

        var request = destination[..QueryLength];
        request.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(request, DeviceStatusMethod);
        BinaryPrimitives.WriteUInt32LittleEndian(request[4..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(request[8..], (uint)control);
        if (IsCurve(control))
        {
            BinaryPrimitives.WriteInt32LittleEndian(request[12..], profile switch { 1 => 2, 2 => 1, _ => 0 });
        }

        return true;
    }

    internal static bool TryReadScalar(ReadOnlySpan<byte> response, out AsusScalar value)
    {
        value = default;
        if (response.Length is < 4 or > ResponseLength)
        {
            return false;
        }

        var raw = BinaryPrimitives.ReadUInt32LittleEndian(response);
        if (raw == 0xFFFFFFFE || (raw & 0x10000) == 0 || (raw & 0xFFF80000) != 0)
        {
            return false;
        }

        value = new AsusScalar(raw, (ushort)raw, raw & 0x70000);
        return true;
    }

    internal static bool TryReadFactoryFanCurve(ReadOnlySpan<byte> response, out FactoryFanCurve? curve)
    {
        curve = null;
        if (response.Length is not (QueryLength or ResponseLength))
        {
            return false;
        }

        for (var i = 0; i < 8; i++)
        {
            if (response[i] is 0 or > 127 || response[8 + i] > 100 || (i > 0 && response[i] < response[i - 1]))
            {
                return false;
            }
        }

        curve = new FactoryFanCurve(
            [.. response[..8]],
            [.. response.Slice(8, 8)]);
        return true;
    }
}
