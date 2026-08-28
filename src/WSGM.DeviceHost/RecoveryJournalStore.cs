using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.DeviceHost;

/// <summary>Crash-safe per-user recovery journal for one immutable plugin package identity.</summary>
internal sealed class RecoveryJournalStore : IAsyncDisposable
{
    private const int MaxJournalBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 1000;
    private readonly string _packageId;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<RecoveryJournalEntry> _entries = [];

    private RecoveryJournalStore(string packageId, string path)
    {
        _packageId = packageId;
        _path = path;
    }

    internal bool CorruptionQuarantined { get; private set; }

    internal IReadOnlyList<RecoveryJournalEntry> Outstanding =>
        JournalReconciliation.Outstanding(_entries).ToArray();

    internal static RecoveryJournalStore Open(string packageId)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSGM",
            JournalPolicy.Default.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        return Open(packageId, root);
    }

    internal static RecoveryJournalStore Open(string packageId, string root)
    {
        if (string.IsNullOrWhiteSpace(packageId) || packageId.Length > 128)
        {
            throw new InvalidDataException("Recovery journal package identity is invalid.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Directory.CreateDirectory(root);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageId)))[..24];
        RecoveryJournalStore store = new(packageId, Path.Combine(root, $"{key}.journal.json"));
        store.Load();
        return store;
    }

    internal async ValueTask PersistAsync(
        RecoveryJournalEntry entry,
        long currentHostGeneration,
        long currentDeviceGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateEntry(entry);
        if (!string.Equals(entry.PackageId, _packageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Recovery entry belongs to another package.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int existingIndex = _entries.FindIndex(candidate => candidate.Sequence == entry.Sequence);
            if (existingIndex >= 0)
            {
                RecoveryJournalEntry existing = _entries[existingIndex];
                if (!SameOperation(existing, entry)
                    || !ValidStatusTransition(existing.Status, entry.Status))
                {
                    throw new InvalidDataException(
                        "Recovery entry replacement changed identity or regressed state.");
                }

                _entries[existingIndex] = entry;
            }
            else
            {
                long maximum = _entries.Count == 0 ? 0 : _entries.Max(candidate => candidate.Sequence);
                if (entry.HostGeneration != currentHostGeneration
                    || entry.DeviceGeneration != currentDeviceGeneration
                    || entry.Sequence <= maximum
                    || entry.Status is not JournalEntryStatus.Planned)
                {
                    throw new InvalidDataException(
                        "New recovery entries must be current-generation monotonic planned records.");
                }

                _entries.Add(entry);
            }

            PruneClosedEntries();
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            using FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > MaxJournalBytes)
            {
                throw new InvalidDataException("Recovery journal exceeds 4 MiB.");
            }

            RecoveryJournalDocument document = JsonSerializer.Deserialize(
                stream,
                RecoveryJournalJsonContext.Default.RecoveryJournalDocument)
                ?? throw new InvalidDataException("Recovery journal was empty.");
            if (document.SchemaVersion != 1
                || !string.Equals(document.PackageId, _packageId, StringComparison.Ordinal)
                || document.Entries.Count > MaxEntries)
            {
                throw new InvalidDataException("Recovery journal header or entry count is invalid.");
            }

            long previous = 0;
            foreach (RecoveryJournalEntry entry in document.Entries.OrderBy(entry => entry.Sequence))
            {
                ValidateEntry(entry);
                if (entry.Sequence <= previous)
                {
                    throw new InvalidDataException(
                        "Recovery journal sequences are duplicated or unordered.");
                }

                previous = entry.Sequence;
            }

            _entries = document.Entries.OrderBy(entry => entry.Sequence).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            string quarantine = _path
                + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            try
            {
                File.Move(_path, quarantine);
            }
            catch (IOException)
            {
            }

            _entries = [];
            CorruptionQuarantined = true;
            Console.Error.WriteLine($"DeviceHost quarantined a corrupt recovery journal: {ex.Message}");
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        RecoveryJournalDocument document = new()
        {
            SchemaVersion = 1,
            PackageId = _packageId,
            Entries = _entries.OrderBy(entry => entry.Sequence).ToArray(),
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            RecoveryJournalJsonContext.Default.RecoveryJournalDocument);
        if (bytes.Length > MaxJournalBytes)
        {
            throw new InvalidDataException("Recovery journal exceeds 4 MiB.");
        }

        string temporary = _path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    private void PruneClosedEntries()
    {
        RecoveryJournalEntry[] closed = _entries.Where(entry => entry.Status
            is JournalEntryStatus.RestoredVerified or JournalEntryStatus.Planned)
            .OrderByDescending(entry => entry.Sequence)
            .Skip(JournalPolicy.Default.RetainedClosedEntries)
            .ToArray();
        foreach (RecoveryJournalEntry entry in closed)
        {
            _entries.Remove(entry);
        }

        if (_entries.Count > MaxEntries)
        {
            throw new InvalidDataException("Recovery journal has too many unresolved entries.");
        }
    }

    private static void ValidateEntry(RecoveryJournalEntry entry)
    {
        if (entry.Sequence <= 0
            || string.IsNullOrWhiteSpace(entry.PackageId) || entry.PackageId.Length > 128
            || string.IsNullOrWhiteSpace(entry.DeviceId) || entry.DeviceId.Length > 128
            || string.IsNullOrWhiteSpace(entry.ResourceId) || entry.ResourceId.Length > 128
            || entry.CapabilityId?.Length > 128
            || entry.FirmwareIdentity?.Length > 256
            || entry.HostGeneration <= 0
            || entry.DeviceGeneration <= 0)
        {
            throw new InvalidDataException("Recovery journal entry is malformed or oversized.");
        }
    }

    private static bool SameOperation(RecoveryJournalEntry left, RecoveryJournalEntry right) =>
        left.Sequence == right.Sequence
        && left.PackageId == right.PackageId
        && left.DeviceId == right.DeviceId
        && left.HostGeneration == right.HostGeneration
        && left.DeviceGeneration == right.DeviceGeneration
        && left.ResourceId == right.ResourceId
        && left.CapabilityId == right.CapabilityId
        && left.FirmwareIdentity == right.FirmwareIdentity
        && ValueEquals(left.OriginalValue, right.OriginalValue)
        && ValueEquals(left.PlannedValue, right.PlannedValue)
        && left.OpenedAt == right.OpenedAt;

    private static bool ValidStatusTransition(JournalEntryStatus previous, JournalEntryStatus next)
    {
        if (previous == next)
        {
            return true;
        }

        return previous switch
        {
            JournalEntryStatus.Planned => next is JournalEntryStatus.Applying,
            JournalEntryStatus.Applying => next is JournalEntryStatus.AppliedVerified
                or JournalEntryStatus.AppliedUnverified
                or JournalEntryStatus.RestoredVerified
                or JournalEntryStatus.RestoredUnverified
                or JournalEntryStatus.RestoreFailed,
            JournalEntryStatus.AppliedVerified or JournalEntryStatus.AppliedUnverified
                or JournalEntryStatus.Abandoned => next is JournalEntryStatus.AppliedVerified
                    or JournalEntryStatus.RestoredVerified
                    or JournalEntryStatus.RestoredUnverified
                    or JournalEntryStatus.RestoreFailed,
            JournalEntryStatus.RestoredUnverified => next is JournalEntryStatus.RestoredVerified
                or JournalEntryStatus.RestoreFailed,
            _ => false,
        };
    }

    private static bool ValueEquals(
        CapabilityValue? left,
        CapabilityValue? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.Kind == right.Kind
            && left.BooleanValue == right.BooleanValue
            && left.IntegerValue == right.IntegerValue
            && left.ChoiceValue == right.ChoiceValue
            && left.ColorValue == right.ColorValue
            && left.CurveValue.SequenceEqual(right.CurveValue);
    }
}

internal sealed record RecoveryJournalDocument
{
    public required int SchemaVersion { get; init; }

    public required string PackageId { get; init; }

    public IReadOnlyList<RecoveryJournalEntry> Entries { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RecoveryJournalDocument))]
internal sealed partial class RecoveryJournalJsonContext : JsonSerializerContext;
