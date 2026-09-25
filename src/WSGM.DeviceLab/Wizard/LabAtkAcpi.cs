using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WSGM.DeviceLab.Wizard;

/// <summary>An ASUS power state the lab captured before changing it.</summary>
/// <param name="Mode">Performance mode.</param>
/// <param name="Spl">Sustained limit in watts.</param>
/// <param name="Sppt">Slow boost limit in watts.</param>
/// <param name="Fppt">Fast boost limit in watts.</param>
/// <param name="CpuCurve">CPU fan curve for <paramref name="Mode" />, as hex.</param>
/// <param name="GpuCurve">GPU fan curve for <paramref name="Mode" />, as hex.</param>
internal sealed record LabAsusState(int Mode, int Spl, int Sppt, int Fppt, string CpuCurve, string GpuCurve)
{
    /// <summary>Whether two states are the same in every field.</summary>
    /// <param name="other">The other state.</param>
    /// <returns>True when equal.</returns>
    public bool Same(LabAsusState other)
    {
        return Mode == other.Mode && Spl == other.Spl && Sppt == other.Sppt && Fppt == other.Fppt
               && string.Equals(CpuCurve, other.CpuCurve, StringComparison.OrdinalIgnoreCase)
               && string.Equals(GpuCurve, other.GpuCurve, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One fan reading.</summary>
/// <param name="Name">Which fan.</param>
/// <param name="Value">The reading.</param>
/// <param name="Unit">Its unit, or <c>raw</c> when the unit is not known.</param>
internal sealed record LabFanReading(string Name, int Value, string Unit);

/// <summary>The raw ATKACPI device call, so the logic above it can be tested without hardware.</summary>
internal interface ILabAtkAcpiChannel : IDisposable
{
    /// <summary>Sends one IOCTL.</summary>
    /// <param name="input">Input buffer.</param>
    /// <param name="output">Output buffer.</param>
    /// <param name="returned">Bytes returned.</param>
    /// <param name="error">The Win32 error when the call failed.</param>
    /// <returns>Whether the call succeeded.</returns>
    bool Control(byte[] input, byte[] output, out uint returned, out int error);
}

/// <summary>
///     ASUS ATKACPI power, performance mode, fan curve and charge limit access, ported from AllyXLab's
///     AsusControl. Every call is logged before and after; only IDs the curated record names are allowed.
/// </summary>
/// <remarks>
///     A write's transport return is not a readback, so every write is followed by a read. A write whose
///     readback does not match stops the sequence without a retry.
/// </remarks>
internal sealed class LabAtkAcpi : IDisposable
{
    /// <summary>The ATKACPI device path.</summary>
    public const string DevicePath = @"\\.\ATKACPI";

    /// <summary>The ATKACPI control code.</summary>
    public const uint IoControlCode = 0x0022240C;

    private const uint SetMethod = 0x53564544; // "DEVS"
    private const uint GetMethod = 0x53545344; // "DSTS"
    private readonly ILabAtkAcpiChannel _channel;
    private readonly LabPowerLog _log;
    private readonly Action<int> _pause;

    /// <summary>Creates the transport over a channel.</summary>
    /// <param name="channel">The device channel; owned and disposed by this instance.</param>
    /// <param name="layout">IDs from the curated record.</param>
    /// <param name="log">Where every call is logged.</param>
    /// <param name="pause">Waits between a write and its readback; <see cref="Thread.Sleep(int)" /> by default.</param>
    internal LabAtkAcpi(ILabAtkAcpiChannel channel, LabAsusLayout layout, LabPowerLog log, Action<int>? pause = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _pause = pause ?? Thread.Sleep;
    }

    /// <summary>The IDs this transport may use.</summary>
    public LabAsusLayout Layout { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _channel.Dispose();
    }

    /// <summary>Opens the ATKACPI device.</summary>
    /// <param name="layout">IDs from the curated record.</param>
    /// <param name="log">Where every call is logged.</param>
    /// <returns>The transport.</returns>
    /// <exception cref="Win32Exception">The device is not there or cannot be opened.</exception>
    public static LabAtkAcpi Open(LabAsusLayout layout, LabPowerLog log)
    {
        var channel = DeviceChannel.Open();
        log.Add("acpi-open", new { Device = DevicePath });
        return new LabAtkAcpi(channel, layout, log);
    }

    /// <summary>Reads a scalar.</summary>
    /// <param name="id">A declared ID.</param>
    /// <returns>The low 16 bits of a supported result.</returns>
    public int Get(uint id)
    {
        var raw = BinaryPrimitives.ReadUInt32LittleEndian(Call(false, id, new byte[4]));
        if ((raw & 0xFFFF0000) != 0x00010000)
        {
            throw new InvalidOperationException($"ASUS {id:X8} did not report a supported value ({raw:X8}).");
        }

        return (int)(raw & 0xFFFF);
    }

    /// <summary>Reads a scalar, or null when it cannot be read.</summary>
    /// <param name="id">A declared ID.</param>
    /// <returns>The value, or null.</returns>
    public int? TryGet(uint id)
    {
        try
        {
            return Get(id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            _log.Add("acpi-read-unavailable", new { Id = Hex(id), ex.Message });
            return null;
        }
    }

    /// <summary>Writes a scalar inside the reviewed bounds. The return is not a readback.</summary>
    /// <param name="id">A declared ID.</param>
    /// <param name="value">The value.</param>
    public void Set(uint id, int value)
    {
        var allowed = id == Layout.Mode
            ? Layout.ModeValues.Contains(value)
            : id == Layout.Charge
                ? value is >= 20 and <= 100
                : id == Layout.Spl || id == Layout.Sppt || id == Layout.Fppt
                    ? value is >= 5 and <= 65
                    : false;
        if (!allowed)
        {
            throw new InvalidOperationException($"Refused ASUS write {Hex(id)} = {value}: outside the reviewed bounds.");
        }

        var args = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(args, value);
        _ = Call(true, id, args);
    }

    /// <summary>Writes a scalar, waits, reads it back, and throws when it does not match.</summary>
    /// <param name="id">A declared ID.</param>
    /// <param name="value">The value.</param>
    public void SetVerified(uint id, int value)
    {
        Set(id, value);
        _pause(120);
        var observed = Get(id);
        _log.Add("readback", new { Id = Hex(id), Expected = value, Observed = observed, Matches = observed == value });
        if (observed != value)
        {
            throw new InvalidOperationException(
                $"The readback of {Hex(id)} was {observed}, not {value}. Stopped without trying again.");
        }
    }

    /// <summary>Reads a fan curve for a performance mode.</summary>
    /// <param name="id">A curve ID.</param>
    /// <param name="mode">Performance mode (0 balanced, 1 performance, 2 silent).</param>
    /// <returns>The 16-byte curve.</returns>
    public byte[] GetCurve(uint id, int mode)
    {
        if (id != Layout.CpuCurve && id != Layout.GpuCurve)
        {
            throw new InvalidOperationException("Not a declared fan curve.");
        }

        // The curve getter's profile selector swaps performance and silent (AllyXLab, HHD).
        var args = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(args, mode switch
        {
            0 => 0,
            1 => 2,
            2 => 1,
            _ => throw new InvalidOperationException("Unknown fan profile selector.")
        });
        var curve = Call(false, id, args);
        return ValidCurve(curve)
            ? curve
            : throw new InvalidOperationException("The fan curve readback is not a restorable eight-point curve.");
    }

    /// <summary>Writes a validated fan curve. The return is not a readback.</summary>
    /// <param name="id">A curve ID.</param>
    /// <param name="curve">Eight temperatures, then eight duties.</param>
    public void SetCurve(uint id, byte[] curve)
    {
        if ((id != Layout.CpuCurve && id != Layout.GpuCurve) || !ValidCurve(curve))
        {
            throw new InvalidOperationException("Refused an invalid fan curve.");
        }

        _ = Call(true, id, curve);
    }

    /// <summary>AllyXLab's curve check: rising temperatures in 20-110 °C, duties up to 100, not all zero.</summary>
    /// <param name="curve">The curve.</param>
    /// <returns>Whether it may be written.</returns>
    public static bool ValidCurve(byte[] curve)
    {
        return curve.Length == 16
               && curve.Take(8).All(x => x is >= 20 and <= 110)
               && curve.Skip(8).All(x => x <= 100)
               && Enumerable.Range(1, 7).All(i => curve[i] >= curve[i - 1])
               && curve.Skip(8).Any(x => x > 0);
    }

    /// <summary>Reads the whole power state and checks it can be put back.</summary>
    /// <returns>The state.</returns>
    public LabAsusState Snapshot()
    {
        if (!Layout.CanSnapshot)
        {
            throw new InvalidOperationException("The record does not declare the whole ASUS power state.");
        }

        int mode = Get(Layout.Mode!.Value), spl = Get(Layout.Spl!.Value), sppt = Get(Layout.Sppt!.Value),
            fppt = Get(Layout.Fppt!.Value);
        if (!Layout.ModeValues.Contains(mode) || spl is < 5 or > 65 || sppt < spl || sppt > 65 || fppt < sppt
            || fppt > 65)
        {
            throw new InvalidOperationException("The power readback is outside the range that can be put back.");
        }

        var state = new LabAsusState(mode, spl, sppt, fppt,
            Convert.ToHexString(GetCurve(Layout.CpuCurve!.Value, mode)),
            Convert.ToHexString(GetCurve(Layout.GpuCurve!.Value, mode)));
        _log.Add("snapshot", state);
        return state;
    }

    /// <summary>Takes two snapshots a moment apart and refuses a state another program is changing.</summary>
    /// <returns>The stable state.</returns>
    public LabAsusState StableSnapshot()
    {
        var first = Snapshot();
        _pause(150);
        return first.Same(Snapshot())
            ? first
            : throw new InvalidOperationException(
                "The power settings changed by themselves. Another program may be controlling them.");
    }

    /// <summary>Sets all three limits to one value in AllyXLab's safe order, each with a readback.</summary>
    /// <param name="original">The state before.</param>
    /// <param name="watts">The value.</param>
    public void SetLimits(LabAsusState original, int watts)
    {
        uint spl = Layout.Spl!.Value, sppt = Layout.Sppt!.Value, fppt = Layout.Fppt!.Value;
        uint[] order = watts <= original.Sppt ? [spl, sppt, fppt] : [fppt, sppt, spl];
        foreach (var id in order)
        {
            SetVerified(id, watts);
        }
    }

    /// <summary>
    ///     Writes the fan test curve (<see cref="LabPowerPlan.FanTestCurve" />) to one fan and reads it back
    ///     for the current mode. One fan is written at a time, as AllyXLab's fan test does.
    /// </summary>
    /// <param name="original">The state before; its mode selects the curve.</param>
    /// <param name="cpu">True for the CPU fan, false for the GPU fan.</param>
    /// <returns>The test curve, as hex.</returns>
    public string ApplyFanTest(LabAsusState original, bool cpu)
    {
        var id = cpu ? Layout.CpuCurve!.Value : Layout.GpuCurve!.Value;
        var hex = cpu ? original.CpuCurve : original.GpuCurve;
        var target = LabPowerPlan.FanTestCurve(Convert.FromHexString(hex))
                     ?? throw new InvalidOperationException("The original fan curve is not valid, so no test curve was written.");
        SetCurve(id, target);
        _pause(150);
        var readback = GetCurve(id, original.Mode);
        var matches = readback.SequenceEqual(target);
        _log.Add("readback", new
        {
            Id = Hex(id),
            Expected = Convert.ToHexString(target),
            Observed = Convert.ToHexString(readback),
            Matches = matches
        });
        return matches
            ? Convert.ToHexString(target)
            : throw new InvalidOperationException("The fan curve did not read back as written. Stopped without trying again.");
    }

    /// <summary>
    ///     Puts the original state back in AllyXLab's order: mode first, then the limits in an order that
    ///     keeps SPL &lt;= SPPT &lt;= FPPT, then both curves. Each write is made only when the value differs,
    ///     once, and read back.
    /// </summary>
    /// <param name="original">The captured state.</param>
    /// <returns>Whether a final snapshot matches the original exactly.</returns>
    public bool Restore(LabAsusState original)
    {
        var ok = true;
        var modeId = Layout.Mode!.Value;
        try
        {
            if (Get(modeId) != original.Mode)
            {
                Set(modeId, original.Mode);
                _pause(150);
            }

            if (Get(modeId) != original.Mode)
            {
                throw new InvalidOperationException(
                    "The original mode could not be put back, so the fan curves were not written.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            _log.Add("restore-error", ex.Message);
            return false;
        }

        try
        {
            uint spl = Layout.Spl!.Value, sppt = Layout.Sppt!.Value, fppt = Layout.Fppt!.Value;
            int currentSpl = Get(spl), currentSppt = Get(sppt), currentFppt = Get(fppt);
            if (currentSpl is < 5 or > 65 || currentSppt < currentSpl || currentFppt < currentSppt
                || currentFppt > 65)
            {
                throw new InvalidOperationException("The current limits are unknown, so none was written.");
            }

            (uint Id, int Value)[] order = original.Fppt < currentSppt
                ? [(spl, original.Spl), (sppt, original.Sppt), (fppt, original.Fppt)]
                : original.Sppt < currentSpl
                    ? [(fppt, original.Fppt), (spl, original.Spl), (sppt, original.Sppt)]
                    : [(fppt, original.Fppt), (sppt, original.Sppt), (spl, original.Spl)];
            foreach (var (id, value) in order)
            {
                if (Get(id) == value)
                {
                    continue;
                }

                Set(id, value);
                _pause(150);
                if (Get(id) != value)
                {
                    throw new InvalidOperationException(
                        "A limit did not read back after it was put back, so the rest were not written.");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            ok = false;
            _log.Add("restore-error", ex.Message);
        }

        // Each curve is independent of the other's failure, but both need the original mode.
        foreach (var (id, hex) in new[]
                 {
                     (Layout.CpuCurve!.Value, original.CpuCurve),
                     (Layout.GpuCurve!.Value, original.GpuCurve)
                 })
        {
            try
            {
                var curve = Convert.FromHexString(hex);
                if (Get(modeId) != original.Mode)
                {
                    throw new InvalidOperationException("The mode changed while the settings were put back.");
                }

                if (!GetCurve(id, original.Mode).SequenceEqual(curve))
                {
                    SetCurve(id, curve);
                    _pause(150);
                }

                if (!GetCurve(id, original.Mode).SequenceEqual(curve))
                {
                    throw new InvalidOperationException("A fan curve did not read back after it was put back.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException
                                           or FormatException)
            {
                ok = false;
                _log.Add("restore-error", ex.Message);
            }
        }

        try
        {
            var now = Snapshot();
            var matches = now.Same(original);
            _log.Add("restore-readback", new { State = now, Matches = matches });
            return ok && matches;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            _log.Add("restore-error", ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Reads every declared scalar getter on its own and both fan curves for each of the three modes,
    ///     as AllyXLab's read-power action does: one unavailable getter never hides the others.
    /// </summary>
    /// <returns>Each reading or why it could not be taken, for evidence.</returns>
    /// <remarks>
    ///     Read-only. The curve getter takes the profile as a DSTS argument (see <see cref="GetCurve" />), so
    ///     reading another mode's curve does not switch the active mode.
    /// </remarks>
    public IReadOnlyList<object> ReadAllGetters()
    {
        List<object> readings = [];
        foreach (var (name, id) in new[]
                 {
                     ("mode", Layout.Mode), ("spl", Layout.Spl), ("sppt", Layout.Sppt), ("fppt", Layout.Fppt),
                     ("cpu-speed", Layout.CpuSpeed), ("gpu-speed", Layout.GpuSpeed), ("charge", Layout.Charge)
                 })
        {
            if (id is not { } value)
            {
                continue;
            }

            try
            {
                readings.Add(new { Getter = name, Id = Hex(value), Value = Get(value) });
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
            {
                readings.Add(new { Getter = name, Id = Hex(value), Unavailable = ex.Message });
            }
        }

        foreach (var mode in new[] { 0, 1, 2 })
        {
            foreach (var (name, id) in new[] { ("cpu-curve", Layout.CpuCurve), ("gpu-curve", Layout.GpuCurve) })
            {
                if (id is not { } value)
                {
                    continue;
                }

                try
                {
                    readings.Add(new
                    {
                        Getter = name,
                        Id = Hex(value),
                        Mode = mode,
                        Curve = Convert.ToHexString(GetCurve(value, mode))
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
                {
                    readings.Add(new { Getter = name, Id = Hex(value), Mode = mode, Unavailable = ex.Message });
                }
            }
        }

        return readings;
    }

    /// <summary>Reads the fan speed IDs. Their unit is not documented, so the values are raw.</summary>
    /// <returns>Readings that could be taken.</returns>
    public IReadOnlyList<LabFanReading> FanSpeeds()
    {
        List<LabFanReading> readings = [];
        foreach (var (name, id) in new[] { ("CPU fan", Layout.CpuSpeed), ("GPU fan", Layout.GpuSpeed) })
        {
            if (id is { } value && TryGet(value) is { } speed)
            {
                readings.Add(new LabFanReading(name, speed, "raw"));
            }
        }

        return readings;
    }

    private static string Hex(uint id)
    {
        return $"0x{id:X8}";
    }

    private byte[] Call(bool write, uint id, byte[] args)
    {
        if (!Layout.AllIds.Contains(id) || (write && (id == Layout.CpuSpeed || id == Layout.GpuSpeed)))
        {
            throw new InvalidOperationException($"Refused an ASUS call to {Hex(id)}: the record does not declare it.");
        }

        var input = new byte[12 + args.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(input, write ? SetMethod : GetMethod);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), (uint)(4 + args.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), id);
        args.CopyTo(input, 12);
        var output = new byte[16];
        _log.Add(write ? "acpi-write" : "acpi-read", new { Id = Hex(id), Args = Convert.ToHexString(args) });
        var success = _channel.Control(input, output, out var returned, out var error);
        _log.Add("acpi-response",
            new { Id = Hex(id), Write = write, Success = success, Returned = returned, Raw = Convert.ToHexString(output) });
        if (!success)
        {
            throw new Win32Exception(error,
                write
                    ? "The ASUS write failed; its effect is unknown and it was not tried again."
                    : "The ASUS read failed.");
        }

        var curveRead = !write && (id == Layout.CpuCurve || id == Layout.GpuCurve);
        if (returned < (curveRead ? 16 : 4) || returned > 16)
        {
            throw new IOException("The ASUS response length is wrong, so there is no verified result.");
        }

        return output;
    }

    private sealed class DeviceChannel : ILabAtkAcpiChannel
    {
        private readonly SafeFileHandle _handle;

        private DeviceChannel(SafeFileHandle handle)
        {
            _handle = handle;
        }

        public bool Control(byte[] input, byte[] output, out uint returned, out int error)
        {
            var success = DeviceIoControl(_handle, IoControlCode, input, (uint)input.Length, output,
                (uint)output.Length, out returned, IntPtr.Zero);
            error = success ? 0 : Marshal.GetLastWin32Error();
            return success;
        }

        public void Dispose()
        {
            _handle.Dispose();
        }

        public static DeviceChannel Open()
        {
            var handle = CreateFile(DevicePath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return new DeviceChannel(handle);
            }

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The ASUS ATKACPI driver is not available.");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint ioctl, byte[] input, uint inputBytes,
            byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);
    }
}
