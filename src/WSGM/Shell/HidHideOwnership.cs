using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Input;

namespace WSGM.Shell;

internal enum HidHideHealthState
{
    Unavailable,
    Inactive,
    Incompatible,
    Ready,
    Faulted,
}

internal sealed class HidHideExactSnapshot
{
    internal HidHideExactSnapshot(
        long revision,
        HidHideHealthState health,
        bool active,
        IEnumerable<string> applications,
        IEnumerable<string> devices,
        string detail = "")
    {
        Revision = revision;
        Health = health;
        Active = active;
        Applications = applications.ToArray();
        Devices = devices.ToArray();
        Detail = detail;
    }

    internal long Revision { get; }

    internal HidHideHealthState Health { get; }

    internal bool Active { get; }

    internal IReadOnlyList<string> Applications { get; }

    internal IReadOnlyList<string> Devices { get; }

    internal string Detail { get; }

    internal bool ExactStateEquals(HidHideExactSnapshot other) =>
        Revision == other.Revision
        && Health == other.Health
        && Active == other.Active
        && Applications.SequenceEqual(other.Applications, StringComparer.Ordinal)
        && Devices.SequenceEqual(other.Devices, StringComparer.Ordinal);
}

internal enum HidHideEntryKind
{
    Application,
    Device,
}

internal enum HidHideMutationKind
{
    Add,
    Remove,
}

internal sealed record HidHideEntryMutation(
    HidHideMutationKind Mutation,
    HidHideEntryKind EntryKind,
    string Value);

internal sealed record HidHideMutationResult(
    bool Applied,
    HidHideExactSnapshot Current,
    string Detail);

internal interface IHidHideAdapter
{
    Task<HidHideExactSnapshot> ReadAsync(CancellationToken cancellationToken);

    Task<HidHideMutationResult> TryMutateAsync(
        HidHideExactSnapshot expected,
        HidHideEntryMutation mutation,
        CancellationToken cancellationToken);
}

internal sealed class DeterministicFakeHidHideAdapter : IHidHideAdapter
{
    private readonly object _gate = new();
    private List<string> _applications;
    private List<string> _devices;
    private long _revision;

    internal DeterministicFakeHidHideAdapter(
        IEnumerable<string>? applications = null,
        IEnumerable<string>? devices = null,
        bool active = true)
    {
        _applications = applications?.ToList() ?? [];
        _devices = devices?.ToList() ?? [];
        Active = active;
        Health = active ? HidHideHealthState.Ready : HidHideHealthState.Inactive;
    }

    internal HidHideHealthState Health { get; set; }

    internal bool Active { get; set; }

    internal Exception? NextReadFailure { get; set; }

    internal Exception? NextMutationFailure { get; set; }

    internal int? FailMutationAttempt { get; set; }

    internal Action<DeterministicFakeHidHideAdapter>? BeforeNextMutation { get; set; }

    internal int ReadCount { get; private set; }

    internal int MutationCount { get; private set; }

    internal int MutationAttemptCount { get; private set; }

    public Task<HidHideExactSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ReadCount++;
            if (NextReadFailure is { } failure)
            {
                NextReadFailure = null;
                throw failure;
            }

            return Task.FromResult(SnapshotUnderGate());
        }
    }

    public Task<HidHideMutationResult> TryMutateAsync(
        HidHideExactSnapshot expected,
        HidHideEntryMutation mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            MutationAttemptCount++;
            BeforeNextMutation?.Invoke(this);
            BeforeNextMutation = null;
            if (FailMutationAttempt == MutationAttemptCount)
            {
                FailMutationAttempt = null;
                throw new IOException("Injected HidHide mutation failure.");
            }

            if (NextMutationFailure is { } failure)
            {
                NextMutationFailure = null;
                throw failure;
            }

            HidHideExactSnapshot current = SnapshotUnderGate();
            if (!current.ExactStateEquals(expected))
            {
                return Task.FromResult(new HidHideMutationResult(
                    false,
                    current,
                    "HidHide changed before the conditional mutation."));
            }

            List<string> entries = mutation.EntryKind is HidHideEntryKind.Application
                ? _applications
                : _devices;
            if (mutation.Mutation is HidHideMutationKind.Add)
            {
                entries.Add(mutation.Value);
            }
            else
            {
                int index = entries.FindIndex(value =>
                    string.Equals(value, mutation.Value, StringComparison.Ordinal));
                if (index < 0)
                {
                    return Task.FromResult(new HidHideMutationResult(
                        false,
                        current,
                        "The exact entry is absent."));
                }

                entries.RemoveAt(index);
            }

            MutationCount++;
            _revision++;
            return Task.FromResult(new HidHideMutationResult(
                true,
                SnapshotUnderGate(),
                "Applied."));
        }
    }

    internal void ExternalReplace(
        IEnumerable<string>? applications = null,
        IEnumerable<string>? devices = null,
        bool? active = null)
    {
        lock (_gate)
        {
            if (applications is not null)
            {
                _applications = applications.ToList();
            }

            if (devices is not null)
            {
                _devices = devices.ToList();
            }

            if (active is { } activeValue)
            {
                Active = activeValue;
                Health = activeValue ? HidHideHealthState.Ready : HidHideHealthState.Inactive;
            }

            _revision++;
        }
    }

    private HidHideExactSnapshot SnapshotUnderGate() => new(
        _revision,
        Health,
        Active,
        _applications,
        _devices,
        Health.ToString());
}

internal enum HidHideOwnedDeltaState
{
    Pending,
    Applied,
    Cleaned,
    CleanupIndeterminate,
}

internal sealed class HidHideOwnedDelta
{
    public HidHideEntryKind EntryKind { get; init; }

    public string Value { get; init; } = string.Empty;

    public HidHideOwnedDeltaState State { get; set; }
}

internal sealed class HidHideOwnershipLedger
{
    public Guid TransactionId { get; init; }

    public long TargetGeneration { get; init; }

    public bool PreexistingActive { get; init; }

    public List<string> PreexistingApplications { get; init; } = [];

    public List<string> PreexistingDevices { get; init; } = [];

    public List<HidHideOwnedDelta> Deltas { get; init; } = [];

    public string? RecoveryDetail { get; set; }
}

internal interface IHidHideOwnershipStore
{
    Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(HidHideOwnershipLedger ledger, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
}

internal sealed class InMemoryHidHideOwnershipStore : IHidHideOwnershipStore
{
    internal HidHideOwnershipLedger? Ledger { get; private set; }

    internal int SaveCount { get; private set; }

    public Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Ledger);
    }

    public Task SaveAsync(HidHideOwnershipLedger ledger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ledger = ledger;
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Ledger = null;
        return Task.CompletedTask;
    }
}

internal sealed class FileHidHideOwnershipStore : IHidHideOwnershipStore
{
    private readonly string _path;

    internal FileHidHideOwnershipStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public async Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using FileStream stream = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
            stream,
            HidHideOwnershipJsonContext.Default.HidHideOwnershipLedger,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        HidHideOwnershipLedger ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The HidHide ledger path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporary = _path + ".new";
        await using (FileStream stream = new(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                ledger,
                HidHideOwnershipJsonContext.Default.HidHideOwnershipLedger,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_path);
        return Task.CompletedTask;
    }
}

internal sealed record HidHideActivationResult(
    bool Activated,
    string Detail,
    HidHideOwnershipLedger? Ledger);

internal sealed record HidHideCleanupResult(
    bool Verified,
    string Detail,
    HidHideOwnershipLedger? RemainingLedger);

internal sealed class HidHideOwnedDeltaManager
{
    private const int MaximumCompareRetries = 3;
    private readonly IHidHideAdapter _adapter;
    private readonly IHidHideOwnershipStore _store;
    private readonly SemaphoreSlim _transition = new(1, 1);

    internal HidHideOwnedDeltaManager(
        IHidHideAdapter adapter,
        IHidHideOwnershipStore store)
    {
        _adapter = adapter;
        _store = store;
    }

    internal async Task<HidHideActivationResult> StartAsync(
        bool controllerManagementEnabled,
        string deviceHostApplication,
        IReadOnlyList<PhysicalDeviceIdentity> physicalDevices,
        long targetGeneration,
        CancellationToken cancellationToken)
    {
        if (!controllerManagementEnabled)
        {
            return new(false, "Controller management is off; HidHide was untouched.", null);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(deviceHostApplication);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _store.LoadAsync(cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return new(false, "A previous HidHide ownership ledger requires recovery.", existing);
            }

            HidHideExactSnapshot snapshot = await _adapter.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Health is not HidHideHealthState.Ready || !snapshot.Active)
            {
                return new(false,
                    $"HidHide prerequisite unavailable: {snapshot.Health} ({snapshot.Detail}).",
                    null);
            }

            HidHideOwnershipLedger ledger = new()
            {
                TransactionId = Guid.NewGuid(),
                TargetGeneration = targetGeneration,
                PreexistingActive = snapshot.Active,
                PreexistingApplications = snapshot.Applications.ToList(),
                PreexistingDevices = snapshot.Devices.ToList(),
            };

            try
            {
                snapshot = await AddIfAbsentAsync(
                    snapshot,
                    ledger,
                    HidHideEntryKind.Application,
                    deviceHostApplication,
                    cancellationToken).ConfigureAwait(false);

                foreach (string instancePath in physicalDevices
                    .Where(device => device.RequiresHiding)
                    .Select(device => device.InstancePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    snapshot = await AddIfAbsentAsync(
                        snapshot,
                        ledger,
                        HidHideEntryKind.Device,
                        instancePath,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!Contains(snapshot.Applications, deviceHostApplication)
                    || physicalDevices.Where(device => device.RequiresHiding)
                        .Any(device => !Contains(snapshot.Devices, device.InstancePath)))
                {
                    throw new InvalidOperationException("HidHide readback did not contain every required entry.");
                }

                return new(true, "WSGM-owned HidHide deltas applied and verified.", ledger);
            }
            catch (Exception ex)
            {
                ledger.RecoveryDetail = $"Activation failed: {ex.Message}";
                await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
                HidHideCleanupResult cleanup = await CleanupUnderGateAsync(ledger, cancellationToken)
                    .ConfigureAwait(false);
                return new(false,
                    cleanup.Verified
                        ? $"HidHide activation rolled back: {ex.Message}"
                        : $"HidHide activation cleanup is unverified: {ex.Message}",
                    cleanup.RemainingLedger);
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task<HidHideCleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HidHideOwnershipLedger? ledger = await _store.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            return ledger is null
                ? new(true, "No WSGM-owned HidHide state exists.", null)
                : await CleanupUnderGateAsync(ledger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task<HidHideCleanupResult> ReconcileAsync(
        Guid transactionId,
        long targetGeneration,
        CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HidHideOwnershipLedger? ledger = await _store.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (ledger is null)
            {
                return new(true, "No WSGM-owned HidHide state exists.", null);
            }

            if (ledger.TransactionId != transactionId
                || ledger.TargetGeneration != targetGeneration)
            {
                ledger.RecoveryDetail = "Recovery refused a different transaction or target generation.";
                await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
                return new(false, ledger.RecoveryDetail, ledger);
            }

            return await CleanupUnderGateAsync(ledger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<HidHideExactSnapshot> AddIfAbsentAsync(
        HidHideExactSnapshot snapshot,
        HidHideOwnershipLedger ledger,
        HidHideEntryKind entryKind,
        string value,
        CancellationToken cancellationToken)
    {
        if (Contains(Entries(snapshot, entryKind), value))
        {
            return snapshot;
        }

        HidHideOwnedDelta delta = new()
        {
            EntryKind = entryKind,
            Value = value,
            State = HidHideOwnedDeltaState.Pending,
        };
        ledger.Deltas.Add(delta);
        await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);

        for (int attempt = 0; attempt < MaximumCompareRetries; attempt++)
        {
            HidHideMutationResult result = await _adapter.TryMutateAsync(
                snapshot,
                new(HidHideMutationKind.Add, entryKind, value),
                cancellationToken).ConfigureAwait(false);
            snapshot = result.Current;
            if (result.Applied)
            {
                delta.State = HidHideOwnedDeltaState.Applied;
                await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
                return snapshot;
            }

            if (Contains(Entries(snapshot, entryKind), value))
            {
                ledger.Deltas.Remove(delta);
                await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
                return snapshot;
            }
        }

        throw new IOException($"HidHide {entryKind} entry kept changing during activation.");
    }

    private async Task<HidHideCleanupResult> CleanupUnderGateAsync(
        HidHideOwnershipLedger ledger,
        CancellationToken cancellationToken)
    {
        List<string> problems = [];
        foreach (HidHideOwnedDelta delta in ledger.Deltas.AsEnumerable().Reverse())
        {
            if (delta.State is HidHideOwnedDeltaState.Cleaned)
            {
                continue;
            }

            bool cleaned = await RemoveOwnedDeltaAsync(delta, cancellationToken)
                .ConfigureAwait(false);
            delta.State = cleaned
                ? HidHideOwnedDeltaState.Cleaned
                : HidHideOwnedDeltaState.CleanupIndeterminate;
            if (!cleaned)
            {
                problems.Add($"{delta.EntryKind}:{delta.Value}");
            }

            await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
        }

        if (problems.Count == 0)
        {
            await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
            return new(true, "Only WSGM-owned HidHide deltas were removed.", null);
        }

        ledger.RecoveryDetail = "Cleanup refused ambiguous entries: " + string.Join(", ", problems);
        await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
        return new(false, ledger.RecoveryDetail, ledger);
    }

    private async Task<bool> RemoveOwnedDeltaAsync(
        HidHideOwnedDelta delta,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < MaximumCompareRetries; attempt++)
        {
            HidHideExactSnapshot snapshot = await _adapter.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Health is not HidHideHealthState.Ready)
            {
                return false;
            }

            IReadOnlyList<string> entries = Entries(snapshot, delta.EntryKind);
            int semanticCount = entries.Count(entry =>
                string.Equals(entry, delta.Value, StringComparison.OrdinalIgnoreCase));
            int exactCount = entries.Count(entry =>
                string.Equals(entry, delta.Value, StringComparison.Ordinal));

            if (semanticCount == 0)
            {
                return true;
            }

            if (semanticCount != 1 || exactCount != 1)
            {
                return false;
            }

            HidHideMutationResult result = await _adapter.TryMutateAsync(
                snapshot,
                new(HidHideMutationKind.Remove, delta.EntryKind, delta.Value),
                cancellationToken).ConfigureAwait(false);
            if (result.Applied)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> Entries(
        HidHideExactSnapshot snapshot,
        HidHideEntryKind entryKind) => entryKind is HidHideEntryKind.Application
            ? snapshot.Applications
            : snapshot.Devices;

    private static bool Contains(IEnumerable<string> entries, string value) =>
        entries.Contains(value, StringComparer.OrdinalIgnoreCase);
}

[JsonSerializable(typeof(HidHideOwnershipLedger))]
internal sealed partial class HidHideOwnershipJsonContext : JsonSerializerContext;
