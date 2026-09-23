using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WSGM.PackagedLaunch;

/// <summary>One package left exempt from lifetime management, and who left it that way.</summary>
/// <remarks>
///     The launcher process id and its start time together identify the owner. A process id alone is
///     not enough: Windows reuses them, and releasing a live launcher's exemption would suspend the
///     game it is supervising.
/// </remarks>
internal sealed class PackageDebugRecord
{
    /// <summary>The package that was exempted.</summary>
    public string PackageFullName { get; set; } = "";

    /// <summary>The launcher that exempted it.</summary>
    public int LauncherProcessId { get; set; }

    /// <summary>When that launcher started, as a round-trip UTC string.</summary>
    public string LauncherStartedUtc { get; set; } = "";

    /// <summary>When the record was written, for diagnostics only.</summary>
    public string WrittenUtc { get; set; } = "";
}

/// <summary>The recovery journal's file shape.</summary>
internal sealed class PackageDebugRecoveryState
{
    /// <summary>Every exemption believed to be in force.</summary>
    public List<PackageDebugRecord> Records { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PackageDebugRecoveryState))]
internal sealed partial class PackageDebugRecoveryJsonContext : JsonSerializerContext;

/// <summary>Releases what this reads: the exemptions a killed launcher could not release itself.</summary>
/// <param name="path">The journal file.</param>
/// <param name="isOwnerAlive">
///     Whether the launcher that wrote a record is still running. Injected so the replay rules are
///     testable without starting processes.
/// </param>
/// <remarks>
///     <para>
///         <c>EnableDebugging</c> takes a package out of Process Lifetime Management, and Windows
///         keeps it out until something calls <c>DisableDebugging</c>. A launcher that exits normally
///         does that itself. One that Steam terminates does not, and the package would stay exempt
///         indefinitely — never suspended when it loses the foreground, for the rest of the machine's
///         life.
///     </para>
///     <para>
///         So the record is written <em>before</em> the exemption is requested and cleared after it is
///         released, and any record whose owner is gone is replayed. The launcher does this at its own
///         startup, before activating anything, and WSGM runs the same sweep through
///         <c>--recover</c> for the user who never launches another packaged game.
///     </para>
///     <para>
///         Not in <c>config.json</c>: that file is user policy under a strict load-or-abort rule, and
///         a recovery write must not be blocked by an unrelated configuration problem.
///     </para>
/// </remarks>
public sealed class PackageDebugRecoveryRecord(string path, Func<int, DateTime?, bool> isOwnerAlive)
{
    /// <summary>The cross-process lock, since WSGM and a launcher can sweep at the same moment.</summary>
    private const string MutexName = @"Local\WSGM.PackagedLaunchRecovery";

    /// <summary>More than this many records means something is wrong, not that many games ran.</summary>
    private const int MaximumRecords = 64;

    private static readonly TimeSpan LockBudget = TimeSpan.FromSeconds(5);

    /// <summary>Where the journal lives beside WSGM's other per-user state.</summary>
    public static string DefaultPath => Path.Combine(
        // wsgm-allow-live-data-path: the launcher is a WSGM component and its recovery journal
        // belongs beside WSGM's own state, not in a directory of its own.
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WSGM",
        "packaged-launch-recovery.json");

    /// <summary>Records an exemption about to be requested.</summary>
    /// <param name="packageFullName">The package being exempted.</param>
    /// <param name="launcherProcessId">This launcher.</param>
    /// <param name="launcherStartedUtc">When this launcher started.</param>
    /// <returns>Whether the record was written. False means no exemption may be requested.</returns>
    public bool Add(string packageFullName, int launcherProcessId, DateTime? launcherStartedUtc)
    {
        return Mutate(state =>
        {
            state.Records.RemoveAll(record => Same(record, packageFullName, launcherProcessId));
            state.Records.Add(new PackageDebugRecord
            {
                PackageFullName = packageFullName,
                LauncherProcessId = launcherProcessId,
                LauncherStartedUtc = Stamp(launcherStartedUtc),
                WrittenUtc = Stamp(DateTime.UtcNow)
            });
            return true;
        });
    }

    /// <summary>Clears one exemption this launcher has just released.</summary>
    /// <param name="packageFullName">The package that was released.</param>
    /// <param name="launcherProcessId">This launcher.</param>
    public void Remove(string packageFullName, int launcherProcessId)
    {
        Mutate(state => state.Records.RemoveAll(record => Same(record, packageFullName, launcherProcessId)) > 0);
    }

    /// <summary>Every package whose owning launcher is gone, and which should be released now.</summary>
    /// <returns>The package full names to release, in the order they were recorded.</returns>
    /// <remarks>
    ///     Taking a record out and releasing its package are separate steps on purpose: the record is
    ///     removed first, so a release that itself fails cannot make the sweep retry forever.
    /// </remarks>
    public IReadOnlyList<string> TakeAbandoned()
    {
        List<string> abandoned = [];
        Mutate(state =>
        {
            var stale = state.Records
                .Where(record => record.PackageFullName.Length > 0
                                 && !isOwnerAlive(record.LauncherProcessId, Parse(record.LauncherStartedUtc)))
                .ToList();
            if (stale.Count == 0)
            {
                return false;
            }

            foreach (var record in stale)
            {
                state.Records.Remove(record);
                if (!abandoned.Contains(record.PackageFullName, StringComparer.OrdinalIgnoreCase))
                {
                    abandoned.Add(record.PackageFullName);
                }
            }

            return true;
        });
        return abandoned;
    }

    private static bool Same(PackageDebugRecord record, string packageFullName, int launcherProcessId)
    {
        return record.LauncherProcessId == launcherProcessId
               && string.Equals(record.PackageFullName, packageFullName, StringComparison.OrdinalIgnoreCase);
    }

    private static string Stamp(DateTime? value)
    {
        return value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
    }

    private static DateTime? Parse(string value)
    {
        // RoundtripKind alone: the stamp is written with "O" and already carries its offset, and
        // combining it with AdjustToUniversal is rejected outright.
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    /// <summary>Applies one change under the cross-process lock.</summary>
    /// <param name="change">The edit, returning whether anything needs writing.</param>
    /// <returns>Whether the journal on disk now reflects the caller's intent.</returns>
    private bool Mutate(Func<PackageDebugRecoveryState, bool> change)
    {
        using Mutex gate = new(false, MutexName);
        var held = false;
        try
        {
            try
            {
                held = gate.WaitOne(LockBudget);
            }
            catch (AbandonedMutexException)
            {
                // A process died holding it. The file is rewritten whole, so the worst an abandoned
                // lock leaves behind is a record this sweep is about to reconsider anyway.
                held = true;
            }

            var state = Read();
            if (change(state))
            {
                Write(state);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Reported rather than swallowed. A caller about to request an exemption has to know
            // that nothing recorded it: an exemption with no record survives every restart of this
            // machine, because only a record can tell a later sweep to put the package back.
            PackagedLaunchLog.Warn($"Package recovery journal unavailable: {ex.Message}");
            return false;
        }
        finally
        {
            if (held)
            {
                gate.ReleaseMutex();
            }
        }
    }

    private PackageDebugRecoveryState Read()
    {
        PackageDebugRecoveryState? state = null;
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            state = JsonSerializer.Deserialize(
                stream, PackageDebugRecoveryJsonContext.Default.PackageDebugRecoveryState);
        }

        state ??= new PackageDebugRecoveryState();
        state.Records ??= [];
        state.Records =
        [
            .. state.Records
                .Where(record => record is { PackageFullName.Length: > 0 and <= 512, LauncherProcessId: > 0 })
                .Take(MaximumRecords)
        ];
        return state;
    }

    private void Write(PackageDebugRecoveryState state)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (state.Records.Count == 0 && File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream, state, PackageDebugRecoveryJsonContext.Default.PackageDebugRecoveryState);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // A later write reuses the same bounded temporary path.
            }
        }
    }
}
