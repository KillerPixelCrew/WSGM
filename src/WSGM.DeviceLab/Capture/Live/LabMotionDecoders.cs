using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Capture.Live;

/// <summary>Where one controller carries its IMU in its HID input report, as HC reads it.</summary>
/// <param name="Name">Short name used in sensor IDs.</param>
/// <param name="VendorId">USB vendor ID.</param>
/// <param name="ProductIds">USB product IDs.</param>
/// <param name="InputReportLength">Collection input report length HC selects, report ID byte included.</param>
/// <param name="BigEndian">Whether the int16 values are big-endian.</param>
/// <param name="Accelerometer">Offsets of the three accelerometer values, in HC's buffer, in report order.</param>
/// <param name="Gyrometer">Offsets of the three gyro values, in HC's buffer, in report order.</param>
/// <param name="AccelerometerScale">g per count.</param>
/// <param name="GyrometerScale">deg/s per count.</param>
/// <param name="Header">Bytes the buffer must start with, as <c>(offset, value)</c>, for packets that share a report.</param>
/// <param name="Reference">HC file the layout comes from.</param>
/// <param name="Note">What HC does before this data flows, which the lab does only on the tester's opt-in.</param>
internal sealed record LabControllerImuLayout(
    string Name,
    ushort VendorId,
    IReadOnlyList<ushort> ProductIds,
    IReadOnlyList<int> InputReportLength,
    bool BigEndian,
    IReadOnlyList<int> Accelerometer,
    IReadOnlyList<int> Gyrometer,
    double AccelerometerScale,
    double GyrometerScale,
    IReadOnlyList<(int Offset, byte Value)> Header,
    string Reference,
    string? Note = null);

/// <summary>
///     Pure decoders for the motion sources that are not Windows sensors: controller HID reports and the
///     CH340 serial IMU. Values are raw axes in report order, scaled to g and deg/s; HC's own axis swaps
///     and signs are left out, because the stage measures those.
/// </summary>
/// <remarks>
///     HC reads through hidapi, which drops the report ID byte when a device has no numbered reports.
///     A raw report always starts with the report ID (0 when unnumbered), so a raw report starting with
///     0 is shifted by one to line up with HC's offsets.
/// </remarks>
internal static class LabMotionDecoders
{
    /// <summary>Length of one serial IMU frame.</summary>
    public const int SerialFrameLength = 23;

    /// <summary>CH340 USB vendor ID.</summary>
    public const ushort SerialVendorId = 0x1A86;

    /// <summary>CH340 USB product ID.</summary>
    public const ushort SerialProductId = 0x7523;

    /// <summary>Controllers whose IMU HC decodes from HID reports.</summary>
    public static IReadOnlyList<LabControllerImuLayout> Controllers { get; } =
    [
        // LegionController.cs: left controller; the right controller has the same values at 48 and 54.
        new("legion-left", 0x17EF, [0x6182, 0x6183, 0x6184, 0x6185, 0x61EB, 0x61EC, 0x61ED, 0x61EE], [64], true,
            [35, 37, 39], [41, 43, 45], 1.0 / 8192, 2000.0 / 32767, [],
            "HandheldCompanion.Controllers.Lenovo/LegionController.cs:281",
            "HC turns the Legion controllers' gyro reports on through its mode commands; the lab sends them only when the tester opts in."),
        new("legion-right", 0x17EF, [0x6182, 0x6183, 0x6184, 0x6185, 0x61EB, 0x61EC, 0x61ED, 0x61EE], [64], true,
            [48, 50, 52], [54, 56, 58], 1.0 / 8192, 2000.0 / 32767, [],
            "HandheldCompanion.Controllers.Lenovo/LegionController.cs:290",
            "HC turns the Legion controllers' gyro reports on through its mode commands; the lab sends them only when the tester opts in."),
        new("legion-go-s", 0x1A86, [0xE310, 0xE311], [33], false,
            [14, 16, 18], [20, 22, 24], 1.0 / 8192, 2000.0 / 32767, [],
            "HandheldCompanion.Controllers.Lenovo/LegionControllerS.cs:81"),
        new("neptune", 0x28DE, [0x1205, 0x12F0], [64, 65], false,
            [24, 26, 28], [30, 32, 34], 2.0 / 32767, 2000.0 / 32767, [(0, 0x01), (1, 0x00), (2, 0x09)],
            "steam-hidapi.net/steam_hidapi.net.Hid/NCInput.cs",
            "Order is pitch, yaw, roll. HC keeps the Deck out of lizard mode by writing every second; the lab does only when the tester opts in."),
        new("steam-controller", 0x28DE, [0x1102, 0x1142], [64, 65], false,
            [28, 30, 32], [34, 36, 38], 2.0 / 32767, 2000.0 / 32767, [(0, 0x01), (2, 0x01)],
            "steam-hidapi.net/steam_hidapi.net.Hid/GCInput.cs",
            "Order is pitch, yaw, roll. HC enables the gyro by writing GYRO_MODE 0x18; the lab writes it only when the tester opts in, so it may read zeros."),
        new("gamesir-tarantula", 0x3537, [], [64], true,
            [15, 17, 19], [21, 23, 25], 1.0 / 8192, 2000.0 / 32767, [],
            "HandheldCompanion.Controllers.GameSir/TarantulaProController.cs:217",
            "HC sends the test mode command 07 04 0A 02 01 first; the lab sends it only when the tester opts in, so it may read nothing.")
    ];

    /// <summary>Finds the layouts that apply to a HID collection.</summary>
    /// <param name="vendorId">USB vendor ID.</param>
    /// <param name="productId">USB product ID.</param>
    /// <param name="inputReportLength">Input report length, report ID included.</param>
    /// <returns>Matching layouts; empty for anything else.</returns>
    public static IEnumerable<LabControllerImuLayout> LayoutsFor(ushort vendorId, ushort productId,
        int inputReportLength)
    {
        return Controllers.Where(layout => layout.VendorId == vendorId
                                           && (layout.ProductIds.Count == 0 || layout.ProductIds.Contains(productId))
                                           && layout.InputReportLength.Contains(inputReportLength));
    }

    /// <summary>Decodes a controller report.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="report">Raw report, starting with the report ID byte.</param>
    /// <param name="accelerometer">Receives three values in g.</param>
    /// <param name="gyrometer">Receives three values in deg/s.</param>
    /// <returns>Whether the report carries the IMU packet.</returns>
    public static bool TryDecode(
        LabControllerImuLayout layout,
        ReadOnlySpan<byte> report,
        Span<double> accelerometer,
        Span<double> gyrometer)
    {
        if (report.Length == 0)
        {
            return false;
        }

        // hidapi drops a zero report ID; HC's offsets count from what is left.
        var buffer = report[0] == 0 ? report[1..] : report;
        foreach (var (offset, value) in layout.Header)
        {
            if (offset >= buffer.Length || buffer[offset] != value)
            {
                return false;
            }
        }

        var last = Math.Max(layout.Accelerometer.Max(), layout.Gyrometer.Max()) + 1;
        if (last >= buffer.Length)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            accelerometer[i] = Read(buffer, layout.Accelerometer[i], layout.BigEndian) * layout.AccelerometerScale;
            gyrometer[i] = Read(buffer, layout.Gyrometer[i], layout.BigEndian) * layout.GyrometerScale;
        }

        return true;
    }

    /// <summary>Finds the next complete serial IMU frame in a byte stream.</summary>
    /// <param name="data">Received bytes.</param>
    /// <param name="start">Where the frame starts, when one was found.</param>
    /// <returns>
    ///     Whether a whole frame is present. When not, <paramref name="start" /> is where to keep bytes from
    ///     (a possible frame start, or the end).
    /// </returns>
    public static bool FindSerialFrame(ReadOnlySpan<byte> data, out int start)
    {
        for (start = 0; start < data.Length; start++)
        {
            if (data[start] != 0xA4)
            {
                continue;
            }

            if (start + 4 > data.Length)
            {
                return false;
            }

            if (data[start + 1] == 0x03 && data[start + 2] == 0x08 && data[start + 3] == 0x12)
            {
                return start + SerialFrameLength <= data.Length;
            }
        }

        return false;
    }

    /// <summary>Decodes one serial IMU frame (<c>A4 03 08 12</c>, then big-endian int16 values).</summary>
    /// <param name="frame">At least <see cref="SerialFrameLength" /> bytes.</param>
    /// <param name="accelerometer">Receives the three accelerometer values in report order, in g.</param>
    /// <param name="gyrometer">Receives the three gyro values in report order, in deg/s.</param>
    /// <returns>Whether the checksum byte equals the low byte of the sum of the others.</returns>
    /// <remarks>
    ///     HC (<c>Sensors/SerialUSBIMU.cs</c>) reads the values as accX, accZ, accY, gyroX, gyroZ, gyroY at
    ///     ±16 g and ±2000 deg/s and does not check the last byte; the lab records the check as evidence.
    /// </remarks>
    public static bool DecodeSerialFrame(ReadOnlySpan<byte> frame, Span<double> accelerometer,
        Span<double> gyrometer)
    {
        for (var i = 0; i < 3; i++)
        {
            accelerometer[i] = BinaryPrimitives.ReadInt16BigEndian(frame[(4 + 2 * i)..]) / 32768.0 * 16.0;
            gyrometer[i] = BinaryPrimitives.ReadInt16BigEndian(frame[(10 + 2 * i)..]) / 32768.0 * 2000.0;
        }

        byte sum = 0;
        for (var i = 0; i < SerialFrameLength - 1; i++)
        {
            sum += frame[i];
        }

        return sum == frame[SerialFrameLength - 1];
    }

    /// <summary>Whether the knowledge record assigns a CH340 serial port to device control.</summary>
    /// <param name="record">The confirmed record.</param>
    /// <returns>True when the serial port must not be opened as an IMU.</returns>
    public static bool SerialAssignedToControl(DeviceKnowledgeRecord? record)
    {
        return record is not null
               && (record.Mechanisms.Any(mechanism =>
                       mechanism.Transport.Contains("serial", StringComparison.OrdinalIgnoreCase))
                   || record.Hazards.Any(hazard => hazard.Contains("CH340", StringComparison.OrdinalIgnoreCase)));
    }

    private static int Read(ReadOnlySpan<byte> buffer, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(buffer[offset..])
            : BinaryPrimitives.ReadInt16LittleEndian(buffer[offset..]);
    }
}
