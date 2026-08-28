using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Trials;

/// <summary>Bounded read result for one trial journal directory.</summary>
public sealed record MutationTrialJournalReadResult
{
    /// <summary>Valid immutable journal entries, sequence ordered.</summary>
    public IReadOnlyList<RecoveryJournalEntry> Entries { get; init; } = [];

    /// <summary>Files that were partial, malformed, oversized, or duplicated.</summary>
    public IReadOnlyList<string> CorruptFiles { get; init; } = [];
}

/// <summary>Crash-preserving append-only trial journal stored only under explicit Device Lab output.</summary>
public sealed class MutationTrialJournal
{
    private const int MaximumEntryBytes = 1_048_576;
    private const int MaximumEntries = 256;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a journal beneath a new explicit Device Lab session directory.</summary>
    /// <param name="sessionDirectory">New directory dedicated to this one trial.</param>
    /// <param name="boundaries">Protected environment paths.</param>
    public MutationTrialJournal(string sessionDirectory, DeviceLabPathBoundaries boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        if (Directory.Exists(sessionDirectory) || File.Exists(sessionDirectory))
        {
            throw new IOException("A mutation-trial session directory must be new.");
        }

        DeviceLabOutputPathDecision decision = DeviceLabOutputPathPolicy.Evaluate(
            sessionDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!decision.IsAllowed || decision.FullPath is null)
        {
            throw new IOException(decision.Reason ?? "Trial session output was rejected.");
        }

        Directory.CreateDirectory(decision.FullPath);
        _directory = Path.Combine(decision.FullPath, "journal");
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Appends and flushes one immutable state before the caller advances hardware.</summary>
    /// <param name="entry">Next monotonically sequenced state.</param>
    /// <param name="cancellationToken">Cancellation before or during the durable write.</param>
    public async Task AppendAsync(
        RecoveryJournalEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "Journal sequence must be positive.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = EntryPath(entry.Sequence);
            if (File.Exists(path))
            {
                throw new IOException($"Journal sequence {entry.Sequence} already exists.");
            }

            await using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(
                stream,
                entry,
                DeviceLabJsonContext.Default.RecoveryJournalEntry,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads bounded immutable records; any partial record remains explicit corruption.</summary>
    /// <returns>Valid records and filenames requiring quarantine.</returns>
    public MutationTrialJournalReadResult Read()
    {
        string[] files = [.. Directory
            .EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)];
        List<RecoveryJournalEntry> entries = [];
        List<string> corrupt = [];
        HashSet<long> sequences = [];

        if (files.Length > MaximumEntries)
        {
            corrupt.Add($"journal contains {files.Length} entries, exceeding {MaximumEntries}");
            files = files[..MaximumEntries];
        }

        foreach (string path in files)
        {
            try
            {
                FileInfo info = new(path);
                if (info.Length is <= 0 or > MaximumEntryBytes)
                {
                    corrupt.Add(Path.GetFileName(path));
                    continue;
                }

                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                RecoveryJournalEntry? entry = JsonSerializer.Deserialize(
                    stream,
                    DeviceLabJsonContext.Default.RecoveryJournalEntry);
                if (entry is null
                    || entry.Sequence <= 0
                    || !sequences.Add(entry.Sequence)
                    || !string.Equals(Path.GetFileName(path), FileName(entry.Sequence), StringComparison.Ordinal))
                {
                    corrupt.Add(Path.GetFileName(path));
                    continue;
                }

                entries.Add(entry);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                corrupt.Add($"{Path.GetFileName(path)}: {exception.GetType().Name}");
            }
        }

        entries.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        return new MutationTrialJournalReadResult
        {
            Entries = entries,
            CorruptFiles = corrupt,
        };
    }

    private string EntryPath(long sequence) => Path.Combine(_directory, FileName(sequence));

    private static string FileName(long sequence) =>
        $"{sequence.ToString("D10", CultureInfo.InvariantCulture)}.json";
}

/// <summary>Fail-closed reconciliation of a trial journal after process death.</summary>
public static class MutationTrialJournalReconciliation
{
    /// <summary>Finds whether the one named resource must be restored, reported, or quarantined.</summary>
    /// <param name="metadata">Reviewed trial owning the journal.</param>
    /// <param name="journal">Bounded journal read.</param>
    /// <param name="currentFirmwareIdentity">Firmware observed now.</param>
    /// <returns>Strictest action across every record and corrupt file.</returns>
    public static ReconciliationAction Decide(
        MutationTrialMetadata metadata,
        MutationTrialJournalReadResult journal,
        string? currentFirmwareIdentity)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.CorruptFiles.Count != 0)
        {
            return ReconciliationAction.Quarantine;
        }

        RecoveryJournalEntry? entry = journal.Entries.OrderByDescending(candidate => candidate.Sequence).FirstOrDefault();
        if (entry is null)
        {
            return ReconciliationAction.None;
        }

        // One directory is one immutable trial transaction. Each later record supersedes the prior
        // phase; treating historical Applying records as separate outstanding mutations would keep
        // a successfully restored resource quarantined forever.
        return string.Equals(entry.ResourceId, metadata.ResourceId, StringComparison.Ordinal)
            ? JournalReconciliation.Decide(entry, currentFirmwareIdentity)
            : ReconciliationAction.Quarantine;
    }
}
