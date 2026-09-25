using System;
using System.Collections.Generic;
using System.Threading;

namespace WSGM.DeviceLab.Transports;

/// <summary>STAPM, fast and slow package power limits in watts.</summary>
/// <param name="Stapm">Sustained (STAPM) limit.</param>
/// <param name="Fast">Fast PPT limit.</param>
/// <param name="Slow">Slow PPT limit.</param>
internal sealed record LabAmdLimits(double Stapm, double Fast, double Slow);

/// <summary>
///     The AMD SMU power limits through PawnIO's pinned RyzenSMU module, for a mobile Ryzen that has no
///     curated record.
/// </summary>
/// <remarks>
///     Command IDs per codename are Handheld Companion 1.3.1.6's
///     (<c>HandheldCompanion.Processors.AMD/RyzenSmuService.cs</c> GetSetStapmCommand, GetSetFastCommand,
///     GetSetSlowCommand): 0x14/0x15/0x16 on Renoir through Krackan Point, 0x1A/0x1B/0x1C on Picasso,
///     Raven and Dali. The module picks the mailbox for the codename. Limits are read back from the PM
///     table (float 0 STAPM, 2 fast, 4 slow, as ryzenadj reads them), never by sending a set command
///     with 0, which is how HC "reads" them and which writes a zero limit. Desktop parts are refused:
///     their IDs and limits differ, and they are not handhelds.
/// </remarks>
internal sealed class LabAmdSmu : IDisposable
{
    /// <summary>Lowest limit the test ever writes.</summary>
    public const double MinimumTestWatts = 5;

    private readonly LabPawnIoModule _module;

    private LabAmdSmu(LabPawnIoModule module, uint codeName, uint smuVersion)
    {
        _module = module;
        CodeName = codeName;
        SmuVersion = smuVersion;
    }

    /// <summary>The module's codename number.</summary>
    public uint CodeName { get; }

    /// <summary>The codename as text.</summary>
    public string CodeNameText => Names.TryGetValue(CodeName, out var name) ? name : $"codename {CodeName}";

    /// <summary>SMU firmware version.</summary>
    public uint SmuVersion { get; }

    /// <summary>The set-command IDs for this codename, or null when the part is not supported.</summary>
    public (uint Stapm, uint Fast, uint Slow)? Commands => CommandsFor(CodeName);

    // PawnIO module codename numbers (HandheldCompanion.Processors.AMD.CpuCodeName).
    private static readonly Dictionary<uint, string> Names = new()
    {
        [1] = "Renoir", [2] = "Picasso", [6] = "Raven Ridge", [7] = "Raven Ridge 2", [10] = "Rembrandt",
        [12] = "Van Gogh", [13] = "Cezanne", [15] = "Dali", [22] = "Lucienne", [23] = "Phoenix",
        [24] = "Phoenix 2", [25] = "Mendocino", [30] = "Hawk Point", [31] = "Strix Point", [32] = "Strix Halo",
        [33] = "Krackan Point"
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _module.Dispose();
    }

    /// <summary>The set-command IDs HC uses for a codename; null for anything that is not a mobile APU.</summary>
    /// <param name="codeName">Module codename number.</param>
    /// <returns>The IDs, or null.</returns>
    public static (uint Stapm, uint Fast, uint Slow)? CommandsFor(uint codeName)
    {
        return codeName switch
        {
            2 or 6 or 7 or 15 => (0x1A, 0x1B, 0x1C),
            1 or 10 or 12 or 13 or 22 or 23 or 24 or 25 or 30 or 31 or 32 or 33 => (0x14, 0x15, 0x16),
            _ => null
        };
    }

    /// <summary>Loads the module and reads the codename and SMU version.</summary>
    /// <returns>The open transport.</returns>
    public static LabAmdSmu Open()
    {
        var module = LabPawnIoModule.LoadRyzenSmu();
        try
        {
            var codeName = (uint)module.Execute("ioctl_get_code_name", [], 1)[0];
            var version = WithPciBus(() => (uint)module.Execute("ioctl_get_smu_version", [], 1)[0]);
            return new LabAmdSmu(module, codeName, version);
        }
        catch
        {
            module.Dispose();
            throw;
        }
    }

    /// <summary>Reads the limits the SMU is enforcing, from a fresh PM table.</summary>
    /// <returns>The limits.</returns>
    public LabAmdLimits ReadLimits()
    {
        var words = WithPciBus(() =>
        {
            _module.Execute("ioctl_resolve_pm_table", [], 2);
            _module.Execute("ioctl_update_pm_table", [], 0);
            return _module.Execute("ioctl_read_pm_table", [], 4);
        });
        if (words.Length < 3)
        {
            throw new InvalidOperationException("The PM table was shorter than expected.");
        }

        var floats = new float[words.Length * 2];
        Buffer.BlockCopy(words, 0, floats, 0, floats.Length * sizeof(float));
        return new LabAmdLimits(Math.Round(floats[0], 2), Math.Round(floats[2], 2), Math.Round(floats[4], 2));
    }

    /// <summary>Whether limits read from the PM table look like limits, so the layout is the one expected.</summary>
    /// <param name="limits">Limits read.</param>
    /// <returns>True when every value is between 3 and 150 W and fast is at least slow.</returns>
    public static bool Plausible(LabAmdLimits limits)
    {
        return limits.Stapm is >= 3 and <= 150 && limits.Fast is >= 3 and <= 150 && limits.Slow is >= 3 and <= 150
               && limits.Fast + 0.5 >= limits.Slow;
    }

    /// <summary>Writes all three limits once each. Never retried; the caller reads back.</summary>
    /// <param name="limits">Limits in watts.</param>
    /// <returns>Each command's first response word, which the SMU echoes as the accepted milliwatts.</returns>
    public IReadOnlyList<long> WriteLimits(LabAmdLimits limits)
    {
        var commands = Commands ?? throw new InvalidOperationException($"{CodeNameText} is not a supported mobile APU.");
        List<long> responses = [];
        foreach (var (command, watts) in new[] { (commands.Stapm, limits.Stapm), (commands.Fast, limits.Fast), (commands.Slow, limits.Slow) })
        {
            if (watts is < MinimumTestWatts or > 150)
            {
                throw new ArgumentOutOfRangeException(nameof(limits), $"{watts} W is outside the tested range.");
            }

            var milliwatts = (long)Math.Round(watts * 1000);
            var response = WithPciBus(() => _module.Execute("ioctl_send_smu_command", [command, milliwatts, 0, 0, 0, 0, 0], 6));
            responses.Add(response.Length > 0 ? response[0] : -1);
        }

        return responses;
    }

    // The same machine-wide PCI mutex LibreHardwareMonitor and Handheld Companion take, so a monitoring
    // tool never interleaves with an SMU mailbox exchange.
    private static T WithPciBus<T>(Func<T> call)
    {
        using var mutex = new Mutex(false, @"Global\Access_PCI");
        var owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned)
            {
                throw new TimeoutException("Another program held the PCI bus for five seconds.");
            }

            return call();
        }
        finally
        {
            if (owned)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
