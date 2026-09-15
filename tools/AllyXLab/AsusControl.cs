using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSGM.AllyXLab;

internal sealed record PowerState(int Mode, int Spl, int Sppt, int Fppt, byte[] CpuCurve, byte[] GpuCurve);

internal sealed class AsusControl : IDisposable
{
    internal const uint Mode = 0x00120075, Spl = 0x001200A3, Sppt = 0x001200A0, Fppt = 0x001200C1;
    internal const uint CpuCurve = 0x00110024, GpuCurve = 0x00110025, CpuSpeed = 0x00110013, GpuSpeed = 0x00110014;
    private readonly SafeFileHandle _handle;
    private readonly SessionLog _log;
    internal AsusControl(SessionLog log)
    {
        _log = log;
        _handle = Hid.CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (_handle.IsInvalid) { _handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "ASUS ATKACPI driver unavailable."); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint ioctl, byte[] input, uint inputBytes,
        byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);

    private byte[] Call(bool write, uint id, byte[] args)
    {
        uint[] allowed = [Mode, Spl, Sppt, Fppt, CpuCurve, GpuCurve, CpuSpeed, GpuSpeed];
        if (!allowed.Contains(id) || (write && id is CpuSpeed or GpuSpeed))
        {
            throw new InvalidOperationException("Unreviewed ASUS operation.");
        }

        byte[] input = new byte[12 + args.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(input, write ? 0x53564544u : 0x53545344u);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), (uint)(4 + args.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), id);
        args.CopyTo(input, 12);
        byte[] output = new byte[16];
        _log.Add(write ? "acpi-write-attempt" : "acpi-read-request", new { Id = $"{id:X8}", Args = Convert.ToHexString(args) });
        bool success = DeviceIoControl(_handle, 0x0022240C, input, (uint)input.Length, output, 16, out uint returned, IntPtr.Zero);
        _log.Add("acpi-response", new { Id = $"{id:X8}", Write = write, Success = success, Returned = returned, Raw = Convert.ToHexString(output) });
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), write ? "ASUS write failed; effect unknown, no retry." : "ASUS read failed.");
        }

        if (returned < (id is CpuCurve or GpuCurve && !write ? 16 : 4) || returned > 16)
        {
            throw new IOException("ASUS response length invalid; no verified result.");
        }

        return output;
    }
    internal int Get(uint id)
    {
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(Call(false, id, new byte[4]));
        if ((raw & 0xFFFF0000) != 0x00010000)
        {
            throw new InvalidOperationException($"ASUS {id:X8} did not report a supported scalar: {raw:X8}.");
        }

        return (int)(raw & 0xFFFF);
    }
    internal void Set(uint id, int value)
    {
        if ((id == Mode && value is < 0 or > 2) || (id != Mode && value is < 5 or > 65))
        {
            throw new InvalidOperationException("Invalid restoration/command value.");
        }

        byte[] args = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(args, value);
        _ = Call(true, id, args); // Transport return is not readback.
    }
    internal byte[] GetCurve(uint id, int mode)
    {
        byte[] args = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(args, mode switch { 1 => 2, 2 => 1, 0 => 0, _ => throw new InvalidOperationException("Unknown fan profile selector.") });
        byte[] curve = Call(false, id, args);
        if (!ValidCurve(curve))
        {
            throw new InvalidOperationException("Fan curve readback is not a restorable eight-point curve.");
        }

        return curve;
    }
    internal void SetCurve(uint id, byte[] curve)
    {
        if (id is not (CpuCurve or GpuCurve) || !ValidCurve(curve))
        {
            throw new InvalidOperationException("Invalid fan curve.");
        }

        _ = Call(true, id, curve);
    }
    internal static bool ValidCurve(byte[] curve) => curve.Length == 16
        && curve.Take(8).All(x => x is >= 20 and <= 110)
        && curve.Skip(8).All(x => x <= 100)
        && Enumerable.Range(1, 7).All(i => curve[i] >= curve[i - 1])
        && curve.Skip(8).Any(x => x > 0);
    internal PowerState Snapshot()
    {
        int mode = Get(Mode), spl = Get(Spl), sppt = Get(Sppt), fppt = Get(Fppt);
        if (mode is < 0 or > 2 || spl is < 5 or > 65 || sppt < spl || sppt > 65 || fppt < sppt || fppt > 65)
        {
            throw new InvalidOperationException("Power readback is outside the reviewed restorable envelope.");
        }

        return new(mode, spl, sppt, fppt, GetCurve(CpuCurve, mode), GetCurve(GpuCurve, mode));
    }
    internal bool Restore(PowerState original)
    {
        bool ok = true;
        try
        {
            if (Get(Mode) != original.Mode)
            {
                Set(Mode, original.Mode);
                Thread.Sleep(150);
            }
            if (Get(Mode) != original.Mode)
            {
                throw new InvalidOperationException("Original profile could not be restored; refusing profile-dependent curve writes.");
            }
        }
        catch (Exception e) { _log.Add("restore-error", e.Message); return false; }

        try
        {
            int currentSpl = Get(Spl), currentSppt = Get(Sppt), currentFppt = Get(Fppt);
            if (currentSpl is < 5 or > 65 || currentSppt < currentSpl || currentFppt < currentSppt || currentFppt > 65)
            {
                throw new InvalidOperationException("Current power envelope is unknown; refusing ordered restoration writes.");
            }

            (uint Id, int Value)[] order = original.Fppt < currentSppt
                ? [(Spl, original.Spl), (Sppt, original.Sppt), (Fppt, original.Fppt)]
                : original.Sppt < currentSpl
                    ? [(Fppt, original.Fppt), (Spl, original.Spl), (Sppt, original.Sppt)]
                    : [(Fppt, original.Fppt), (Sppt, original.Sppt), (Spl, original.Spl)];
            foreach (var item in order)
            {
                if (Get(item.Id) == item.Value)
                {
                    continue;
                }

                Set(item.Id, item.Value);
                Thread.Sleep(150);
                if (Get(item.Id) != item.Value)
                {
                    throw new InvalidOperationException("A limit restoration did not read back. Remaining dependent writes stopped.");
                }
            }
        }
        catch (Exception e) { ok = false; _log.Add("restore-error", e.Message); }

        // Curves are independent of each other's failure, but both require the original profile.
        foreach (var item in new[] { (Id: CpuCurve, Curve: original.CpuCurve), (Id: GpuCurve, Curve: original.GpuCurve) })
        {
            try
            {
                if (Get(Mode) != original.Mode)
                {
                    throw new InvalidOperationException("Profile changed during restoration.");
                }

                if (!GetCurve(item.Id, original.Mode).SequenceEqual(item.Curve))
                {
                    SetCurve(item.Id, item.Curve);
                    Thread.Sleep(150);
                }
                if (!GetCurve(item.Id, original.Mode).SequenceEqual(item.Curve))
                {
                    throw new InvalidOperationException("Fan restoration did not read back.");
                }
            }
            catch (Exception e) { ok = false; _log.Add("restore-error", e.Message); }
        }
        try
        {
            PowerState now = Snapshot();
            bool matches = now.Mode == original.Mode && now.Spl == original.Spl && now.Sppt == original.Sppt && now.Fppt == original.Fppt
                && now.CpuCurve.SequenceEqual(original.CpuCurve) && now.GpuCurve.SequenceEqual(original.GpuCurve);
            _log.Add("restoration-readback", new { State = now, Matches = matches });
            return ok && matches;
        }
        catch (Exception e) { _log.Add("restore-error", e.Message); return false; }
    }
    public void Dispose() => _handle.Dispose();
}
