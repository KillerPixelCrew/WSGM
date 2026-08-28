using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Capabilities;

namespace WSGM.Device.Contracts.Lifecycle;

/// <summary>
/// One durable record of a hardware change, written before the change and updated after it.
/// </summary>
/// <remarks>
/// The journal exists for the case where nothing else can help: the host is killed between writing to
/// hardware and restoring it. On the next start the entry is all that remains of what was changed and
/// what it was before, so it is written *before* the mutation, not after — an entry that only exists
/// once the write succeeded is useless exactly when it is needed.
/// <para>
/// WSGM cannot restore hardware through an implementation it deliberately does not own, so
/// reconciliation is the plugin's: WSGM presents the outstanding entry and the plugin decides what,
/// if anything, is safe to put back.
/// </para>
/// </remarks>
public sealed record RecoveryJournalEntry
{
    /// <summary>
    /// Monotonic sequence number within the journal, assigned before the entry is written.
    /// </summary>
    /// <remarks>
    /// Ordering must survive a crash mid-write, so it comes from the entry rather than from file
    /// order or a timestamp: two entries can share a timestamp, and an interrupted append can leave
    /// them out of order on disk.
    /// </remarks>
    public required long Sequence { get; init; }

    /// <summary>Package that owned the change.</summary>
    public required string PackageId { get; init; }

    /// <summary>Device definition the change applied to.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Host generation that made the change.</summary>
    public required long HostGeneration { get; init; }

    /// <summary>Device generation the change applied to.</summary>
    public required long DeviceGeneration { get; init; }

    /// <summary>Resource that was mutated.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Capability that was mutated, when the change came from one.</summary>
    public string? CapabilityId { get; init; }

    /// <summary>
    /// Firmware identity at the time of the change.
    /// </summary>
    /// <remarks>
    /// Recorded so reconciliation can refuse to restore across a firmware update. Putting an old
    /// value back into a layout that has since moved is worse than leaving it alone.
    /// </remarks>
    public string? FirmwareIdentity { get; init; }

    /// <summary>The value read before the change, and the only thing restoration may use.</summary>
    public CapabilityValue? OriginalValue { get; init; }

    /// <summary>The value the plugin intended to apply.</summary>
    public CapabilityValue? PlannedValue { get; init; }

    /// <summary>The value confirmed applied, when readback was available.</summary>
    public CapabilityValue? AppliedValue { get; init; }

    /// <summary>How far the entry got.</summary>
    public required JournalEntryStatus Status { get; init; }

    /// <summary>When the entry was opened, in UTC.</summary>
    public required DateTimeOffset OpenedAt { get; init; }

    /// <summary>When the entry reached a terminal status, in UTC.</summary>
    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary>How far a journalled change got.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JournalEntryStatus>))]
public enum JournalEntryStatus
{
    /// <summary>Original state captured; nothing written yet.</summary>
    Planned,

    /// <summary>The write was issued and its result is not yet known.</summary>
    Applying,

    /// <summary>Applied, and confirmed by readback.</summary>
    AppliedVerified,

    /// <summary>Applied, with no readback to confirm it.</summary>
    AppliedUnverified,

    /// <summary>Original state was restored and confirmed.</summary>
    RestoredVerified,

    /// <summary>A restore was written but could not be confirmed.</summary>
    RestoredUnverified,

    /// <summary>
    /// Restoration failed. The resource is quarantined and stays so until a person intervenes.
    /// </summary>
    RestoreFailed,

    /// <summary>
    /// The host died between steps and the entry was found open on the next start.
    /// </summary>
    /// <remarks>
    /// Assigned during reconciliation, never by the writer — by definition nothing was running to
    /// write it.
    /// </remarks>
    Abandoned,
}

/// <summary>
/// What reconciliation decided about an entry found open at startup.
/// </summary>
public enum ReconciliationAction
{
    /// <summary>Nothing to do; the entry reached a terminal status cleanly.</summary>
    None,

    /// <summary>Restoration is safe to attempt and should be.</summary>
    Restore,

    /// <summary>
    /// Restoration is not safe; report the outstanding change and leave the hardware alone.
    /// </summary>
    ReportOnly,

    /// <summary>Block the resource until a person decides.</summary>
    Quarantine,
}

/// <summary>
/// Decides what to do with journal entries found outstanding at startup.
/// </summary>
public static class JournalReconciliation
{
    /// <summary>
    /// Decides how to treat one outstanding entry.
    /// </summary>
    /// <param name="entry">The entry as found on disk.</param>
    /// <param name="currentFirmwareIdentity">Firmware identity observed now.</param>
    /// <returns>What reconciliation should do.</returns>
    /// <remarks>
    /// Three rules, each earning its place:
    /// <list type="bullet">
    /// <item>An entry with no captured original value can never be restored. Substituting a plausible
    /// factory default would be inventing a value the device may never have had.</item>
    /// <item>An entry from different firmware is reported, not restored: offsets and ranges may have
    /// moved, so putting an old value back could write it somewhere else entirely.</item>
    /// <item>A failed restore stays quarantined. Retrying automatically is how one failure becomes a
    /// loop of them.</item>
    /// </list>
    /// </remarks>
    public static ReconciliationAction Decide(
        RecoveryJournalEntry entry,
        string? currentFirmwareIdentity)
    {
        ArgumentNullException.ThrowIfNull(entry);

        switch (entry.Status)
        {
            case JournalEntryStatus.RestoredVerified:
                return ReconciliationAction.None;

            case JournalEntryStatus.RestoreFailed:
                return ReconciliationAction.Quarantine;

            case JournalEntryStatus.Planned:
                // Captured but never written: there is nothing on the device to undo.
                return ReconciliationAction.None;
        }

        if (entry.OriginalValue is null)
        {
            return ReconciliationAction.ReportOnly;
        }

        if (entry.FirmwareIdentity is not null
            && !string.Equals(entry.FirmwareIdentity, currentFirmwareIdentity, StringComparison.Ordinal))
        {
            return ReconciliationAction.ReportOnly;
        }

        return ReconciliationAction.Restore;
    }

    /// <summary>
    /// Returns the entries that still need attention, newest first.
    /// </summary>
    /// <param name="entries">All entries read from the journal.</param>
    /// <returns>Entries whose status is not terminal-clean.</returns>
    public static IEnumerable<RecoveryJournalEntry> Outstanding(
        IEnumerable<RecoveryJournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<RecoveryJournalEntry> outstanding = [];
        foreach (RecoveryJournalEntry entry in entries)
        {
            if (entry.Status is not (JournalEntryStatus.RestoredVerified or JournalEntryStatus.Planned))
            {
                outstanding.Add(entry);
            }
        }

        outstanding.Sort((a, b) => b.Sequence.CompareTo(a.Sequence));
        return outstanding;
    }
}
