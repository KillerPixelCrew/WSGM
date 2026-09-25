using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Wizard;

/// <summary>Changes the wizard made to this machine that outlive a session until they are undone.</summary>
internal sealed record LabMachineChanges
{
    /// <summary>Motor routes that may need a zero after a worker stopped unexpectedly.</summary>
    public IReadOnlyList<LabPendingRumbleRoute> Rumble { get; init; } = [];

    /// <summary>A curated init whose result was not confirmed before the wizard stopped.</summary>
    public string? CuratedInitRecordId { get; init; }

    /// <summary>The HidHide allowed-application entry the lab added, or null.</summary>
    public string? HidHideEntry { get; init; }

    /// <summary>When the entry was added.</summary>
    public DateTimeOffset? HidHideAddedAt { get; init; }

    /// <summary>
    ///     Whether the lab installed PawnIO on a machine that had none. Only then may the lab offer to
    ///     remove it again. Set before the installer starts, and reconciled against what is installed
    ///     when the wizard starts, so an install that finished after the window closed is still known.
    /// </summary>
    public bool PawnIoInstalledByLab { get; init; }

    /// <summary>
    ///     The version the lab removed to install the pinned one, or null. A replaced driver belonged to
    ///     the tester, so the lab never offers to remove its successor.
    /// </summary>
    public string? PawnIoReplacedVersion { get; init; }

    /// <summary>
    ///     Power, fan, charge and lighting settings the power stage changed and has not seen put back, or
    ///     null. <see cref="LabPowerRecovery.RestoreRecorded" /> undoes them on the next start.
    /// </summary>
    public LabPowerChanges? Power { get; init; }
}

/// <summary>One motor route recorded before the first worker write.</summary>
internal sealed record LabPendingRumbleRoute(string? RecordId, string RouteId, string Target);

/// <summary>
///     Persists <see cref="LabMachineChanges" /> outside any project, so a killed or crashed session is
///     cleaned up on the next start even if its project folder is gone.
/// </summary>
/// <remarks>
///     Each change is recorded before it is made. A record without the change is harmless (undoing an
///     absent entry is a no-op); a change without a record would be a leak.
/// </remarks>
internal sealed class LabMachineState(string path)
{
    private readonly object _gate = new();

    /// <summary>The default location under the current user's local application data.</summary>
    public static LabMachineState ForCurrentUser { get; } = new(System.IO.Path.Combine(
        // wsgm-allow-live-data-path: Device Lab's own root beside WSGM's data, never inside it.
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WSGM Device Lab",
        "wizard",
        "machine-changes.json"));

    /// <summary>State file path.</summary>
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>Reads the recorded changes; a missing or unreadable file reads as none.</summary>
    /// <returns>The changes.</returns>
    public LabMachineChanges Read()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(Path)
                    ? JsonSerializer.Deserialize<LabMachineChanges>(File.ReadAllText(Path), LabProject.JsonOptions)
                      ?? new LabMachineChanges()
                    : new LabMachineChanges();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new LabMachineChanges();
            }
        }
    }

    /// <summary>Applies a change to the record and writes it durably.</summary>
    /// <param name="change">Maps the current record to the new one.</param>
    /// <returns>The new record.</returns>
    public LabMachineChanges Update(Func<LabMachineChanges, LabMachineChanges> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var updated = change(Read());
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var staging = DurableFile.StagingPath(Path);
            DurableFile.WriteNewText(staging, JsonSerializer.Serialize(updated, LabProject.JsonOptions) + "\n");
            File.Move(staging, Path, true);
            return updated;
        }
    }
}
