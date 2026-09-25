using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LibreHardwareMonitor.PawnIo;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Transports;

/// <summary>The Intel package power limits as the MCHBAR mirror and MSR 0x610 hold them.</summary>
/// <param name="MchbarPl1">MCHBAR <c>0x59A0</c>, 16 bits: bit 15 enable, bits 0-14 PL1 in power units.</param>
/// <param name="MchbarPl2">MCHBAR <c>0x59A4</c>, 16 bits, the same for PL2.</param>
/// <param name="Msr610">MSR 0x610 as one 64-bit value.</param>
/// <param name="PowerUnitWatts">Watts per power unit, from MSR 0x606.</param>
internal sealed record LabIntelLimits(int MchbarPl1, int MchbarPl2, ulong Msr610, double PowerUnitWatts)
{
    /// <summary>PL1 from the MCHBAR mirror, in watts.</summary>
    public double MchbarPl1Watts => (MchbarPl1 & 0x7FFF) * PowerUnitWatts;

    /// <summary>PL1 from MSR 0x610, in watts.</summary>
    public double MsrPl1Watts => (Msr610 & 0x7FFF) * PowerUnitWatts;

    /// <summary>PL2 from MSR 0x610, in watts.</summary>
    public double MsrPl2Watts => ((Msr610 >> 32) & 0x7FFF) * PowerUnitWatts;

    /// <summary>Whether MSR 0x610 is locked (bit 63) until reset.</summary>
    public bool MsrLocked => (Msr610 >> 63) != 0;
}

/// <summary>
///     The Intel KX service the wizard calls through the hardware worker (<c>intel-kx</c>, opened with the
///     power unit in watts, or null to read it from MSR 0x606). Writes are refused until the worker's
///     checkpoint is acknowledged.
/// </summary>
internal interface ILabIntelKx : IDisposable
{
    /// <summary>Reads the MCHBAR mirror and MSR 0x610; also the checkpoint snapshot.</summary>
    /// <returns>The limits.</returns>
    [LabWorkerSnapshot]
    LabIntelLimits Read();

    /// <summary>Writes PL1 in both places, keeping every other bit.</summary>
    /// <param name="original">State read just before.</param>
    /// <param name="watts">New PL1.</param>
    /// <returns>Null when both writes reported success; otherwise what failed.</returns>
    [LabWorkerWrite]
    string? WritePl1(LabIntelLimits original, double watts);

    /// <summary>Puts back the exact original MCHBAR PL1 and MSR 0x610 values.</summary>
    /// <param name="original">State read before the test.</param>
    /// <returns>Null when both writes reported success; otherwise what failed.</returns>
    [LabWorkerWrite]
    string? Restore(LabIntelLimits original);

    /// <summary>Every KX command run so far and its output, for the evidence.</summary>
    /// <returns>The commands, oldest first.</returns>
    IReadOnlyList<string> CommandLog();
}

/// <summary>
///     Intel package power limits through the pinned KX.exe, for an Intel machine with no curated record.
/// </summary>
/// <remarks>
///     The commands and addresses are Handheld Companion 1.3.1.6's (<c>HandheldCompanion.Processors.Intel/KX.cs</c>):
///     MCHBAR found at <c>0xFEDC0000</c> or <c>0xFED10000</c>, PL1 at <c>+0x59A0</c> and PL2 at
///     <c>+0x59A4</c>, and MSR 0x610. Unlike HC, which writes MSR 0x610 with fixed enable and time-window
///     bits, every write here is read-modify-write of the PL1 field only, so the time window, clamp and
///     lock bits stay as they were, and a locked MSR is never written. Each call runs the digest-checked
///     copy from an administrators-only folder with a deadline. It runs only inside the hardware worker,
///     behind <see cref="ILabIntelKx" />.
/// </remarks>
internal sealed class LabIntelKx : ILabIntelKx
{
    /// <summary>The worker service registration.</summary>
    public static LabWorkerService Service { get; } = new("intel-kx", typeof(ILabIntelKx),
        (args, _) => Open(args.Count > 0 ? LabWorkerService.Arg<double?>(args, 0) : null));

    private static readonly string[] MchbarCandidates = ["0xFEDC0000", "0xFED10000"];
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly string _directory;
    private readonly FileStream _held;
    private readonly string _kx;

    private readonly double _powerUnitWatts;

    private LabIntelKx(string directory, string kx, FileStream held, double powerUnitWatts)
    {
        _directory = directory;
        _kx = kx;
        _held = held;
        _powerUnitWatts = powerUnitWatts;
    }

    /// <summary>The MCHBAR base in use, as KX writes it, for example <c>0xFEDC</c>.</summary>
    public string? MchbarPrefix { get; private set; }

    /// <summary>Every command run and its output, for the evidence.</summary>
    public List<string> Log { get; } = [];

    /// <inheritdoc />
    public void Dispose()
    {
        _held.Dispose();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover copy in an administrators-only folder is removed with the Windows temp folder.
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> CommandLog()
    {
        return [.. Log];
    }

    /// <summary>Extracts and checks KX.exe, then finds the MCHBAR.</summary>
    /// <param name="powerUnitWatts">Watts per power unit, or null to read MSR 0x606 through PawnIO.</param>
    /// <returns>The open transport.</returns>
    public static LabIntelKx Open(double? powerUnitWatts)
    {
        var unit = powerUnitWatts ?? PowerUnitWatts();
        var directory = PawnIoSetup.CreateAdministratorsOnlyDirectory("kx");
        var path = Path.Combine(directory, "KX.exe");
        using (var resource = typeof(LabIntelKx).Assembly.GetManifestResourceStream("WSGM.DeviceLab.KX.exe")
                              ?? throw new InvalidOperationException("This build does not include KX.exe."))
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            resource.CopyTo(file);
        }

        // Held without write or delete sharing, so nothing can replace the file after the check.
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = Convert.ToHexString(SHA256.HashData(held));
        if (!string.Equals(digest, PinnedDigest(), StringComparison.OrdinalIgnoreCase))
        {
            held.Dispose();
            throw new InvalidOperationException($"The bundled KX.exe does not match its pin ({digest}).");
        }

        LabIntelKx kx = new(directory, path, held, unit);
        foreach (var candidate in MchbarCandidates)
        {
            if (kx.Return("/rdmem32", candidate) is { } value && value != uint.MaxValue)
            {
                kx.MchbarPrefix = candidate[..6];
                break;
            }
        }

        return kx;
    }

    /// <summary>Reads the MCHBAR mirror and MSR 0x610.</summary>
    /// <returns>The limits, with the power unit the transport was opened with.</returns>
    public LabIntelLimits Read()
    {
        var prefix = MchbarPrefix ?? throw new InvalidOperationException("The MCHBAR could not be found.");
        var pl1 = Return("/rdmem16", prefix + "59A0") ?? throw new InvalidOperationException("MCHBAR PL1 could not be read.");
        var pl2 = Return("/rdmem16", prefix + "59A4") ?? throw new InvalidOperationException("MCHBAR PL2 could not be read.");
        var msr = ReadMsr(0x610) ?? throw new InvalidOperationException("MSR 0x610 could not be read.");
        return new LabIntelLimits((int)pl1, (int)pl2, msr, _powerUnitWatts);
    }

    /// <summary>Writes PL1 in both places, keeping every other bit. Never retried; the caller reads back.</summary>
    /// <param name="original">State read just before.</param>
    /// <param name="watts">New PL1.</param>
    /// <returns>Null when both writes reported success; otherwise what failed.</returns>
    public string? WritePl1(LabIntelLimits original, double watts)
    {
        var units = (int)Math.Round(watts / original.PowerUnitWatts);
        if (units is <= 0 or > 0x7FFF)
        {
            throw new ArgumentOutOfRangeException(nameof(watts));
        }

        var mem = (int)(((uint)original.MchbarPl1 & 0xFFFF8000u) | (uint)units);
        var problem = WriteMem16(MchbarPrefix + "59A0", mem);
        if (problem is not null || original.MsrLocked)
        {
            return problem;
        }

        return WriteMsr(0x610, (original.Msr610 & ~0x7FFFUL) | (uint)units);
    }

    /// <summary>Puts back the exact original MCHBAR PL1 and MSR 0x610 values.</summary>
    /// <param name="original">State read before the test.</param>
    /// <returns>Null when both writes reported success; otherwise what failed.</returns>
    public string? Restore(LabIntelLimits original)
    {
        var problem = WriteMem16(MchbarPrefix + "59A0", original.MchbarPl1);
        return original.MsrLocked ? problem : problem ?? WriteMsr(0x610, original.Msr610);
    }

    private string? WriteMem16(string address, int value)
    {
        var text = $"0x{value:X4}";
        var returned = Return("/wrmem16", address, text);
        return returned == value ? null : $"KX wrote {text} to {address} but returned {returned?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}.";
    }

    private string? WriteMsr(uint index, ulong value)
    {
        var high = $"0x{(uint)(value >> 32):X8}";
        var low = $"0x{(uint)value:X8}";
        Run("/wrmsr", $"0x{index:X}", high, low);
        return ReadMsr(index) == value ? null : $"MSR 0x{index:X} did not read back as {high} {low}.";
    }

    private ulong? ReadMsr(uint index)
    {
        foreach (var line in Run("/rdmsr", $"0x{index:X}"))
        {
            var at = line.IndexOf("Msr Data", StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var parts = line[(line.IndexOf(':', at) + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && ulong.TryParse(parts[0].Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high)
                && ulong.TryParse(parts[1].Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low))
            {
                return (high << 32) | low;
            }
        }

        return null;
    }

    // KX prints "Return <decimal>" for memory reads and writes.
    private long? Return(params string[] arguments)
    {
        foreach (var line in Run(arguments))
        {
            var at = line.IndexOf("Return ", StringComparison.Ordinal);
            if (at >= 0 && long.TryParse(line[(at + 7)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var value))
            {
                return value;
            }
        }

        return null;
    }

    private List<string> Run(params string[] arguments)
    {
        ProcessStartInfo start = new(_kx)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _directory
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("KX.exe did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(Deadline))
        {
            process.Kill(true);
            Log.Add($"{string.Join(' ', arguments)}: no answer within {Deadline.TotalSeconds:0} s");
            return [];
        }

        List<string> lines = [.. output.Result.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        Log.Add($"{string.Join(' ', arguments)} -> exit {process.ExitCode}: {string.Join(" | ", lines)}");
        return lines;
    }

    // MSR 0x606 bits 0-3: power unit is 1 / 2^n watts (usually n = 3, an eighth of a watt).
    private static double PowerUnitWatts()
    {
        var msr = new IntelMsr();
        try
        {
            return msr.ReadMsr(0x606, out ulong value)
                ? 1.0 / (1 << (int)(value & 0xF))
                : throw new InvalidOperationException("MSR 0x606 could not be read through PawnIO.");
        }
        finally
        {
            msr.Close();
        }
    }

    private static string PinnedDigest()
    {
        using var stream = typeof(LabIntelKx).Assembly.GetManifestResourceStream("WSGM.DeviceLab.KX.lock.json")
                           ?? throw new InvalidOperationException("The KX lock file is not embedded.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("component").GetProperty("sha256").GetString()!;
    }
}
