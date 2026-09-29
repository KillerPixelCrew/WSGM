using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;

namespace WSGM.Shell;

internal enum HidHideHealthState
{
    Unavailable,
    Inactive,
    Incompatible,
    Ready,
    Faulted
}

internal static class HidHideHealthStateExtensions
{
    /// <summary>Whether the driver answers and accepts writes: active, or installed with the cloak off.</summary>
    /// <param name="health">The health read from the control device.</param>
    /// <returns>True for <see cref="HidHideHealthState.Ready" /> and <see cref="HidHideHealthState.Inactive" />.</returns>
    internal static bool IsWritable(this HidHideHealthState health)
    {
        return health is HidHideHealthState.Ready or HidHideHealthState.Inactive;
    }
}

internal sealed class HidHideExactSnapshot
{
    internal HidHideExactSnapshot(
        HidHideHealthState health,
        bool active,
        IEnumerable<string> applications,
        IEnumerable<string> devices,
        string detail = "")
    {
        Health = health;
        Active = active;
        Applications = [.. applications];
        Devices = [.. devices];
        Detail = detail;
    }

    internal HidHideHealthState Health { get; }

    internal bool Active { get; }

    internal IReadOnlyList<string> Applications { get; }

    internal IReadOnlyList<string> Devices { get; }

    internal string Detail { get; }

    // Inverse mode needs no field of its own here: it is encoded as Health.Incompatible, so a flip
    // still fails this comparison.
    internal bool ExactStateEquals(HidHideExactSnapshot other)
    {
        return Health == other.Health
               && Active == other.Active
               && Applications.SequenceEqual(other.Applications, StringComparer.Ordinal)
               && Devices.SequenceEqual(other.Devices, StringComparer.Ordinal);
    }
}

internal enum HidHideEntryKind
{
    Application,
    Device
}

internal enum HidHideMutationKind
{
    Add,
    Remove
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

    /// <summary>Turns the cloak on or off when HidHide still matches <paramref name="expected" />.</summary>
    /// <param name="expected">The exact state the caller read.</param>
    /// <param name="active">The cloak state to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Applied only when the readback shows the new cloak state and unchanged lists.</returns>
    Task<HidHideMutationResult> TrySetActiveAsync(
        HidHideExactSnapshot expected,
        bool active,
        CancellationToken cancellationToken);
}

internal enum HidHideOwnedDeltaState
{
    Pending,
    Applied,
    Cleaned,
    CleanupIndeterminate
}

internal sealed class HidHideOwnedDelta
{
    public HidHideEntryKind EntryKind { get; init; }

    public string Value { get; init; } = string.Empty;

    public HidHideOwnedDeltaState State { get; set; }
}

internal sealed class HidHideOwnershipLedger
{
    public List<HidHideOwnedDelta> Deltas { get; init; } = [];

    public string? RecoveryDetail { get; set; }
}

internal interface IHidHideOwnershipStore
{
    Task<HidHideOwnershipLedger?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(HidHideOwnershipLedger ledger, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
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
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The HidHide ledger path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        await AtomicFile.WriteAsync(
            _path,
            (stream, token) => JsonSerializer.SerializeAsync(
                stream,
                ledger,
                HidHideOwnershipJsonContext.Default.HidHideOwnershipLedger,
                token),
            true,
            cancellationToken).ConfigureAwait(false);
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
    string Detail);

internal sealed record HidHideCleanupResult(
    bool Verified,
    string Detail);

internal sealed class HidHideOwnedDeltaManager
{
    private const int MaximumCompareRetries = 3;
    private readonly IHidHideAdapter _adapter;
    private readonly IHidHideOwnershipStore _store;
    private readonly SemaphoreSlim _transition = new(1, 1);

    // Set when this session kept its entries and the cloak on purpose (sleep, fault recovery), so the
    // next start extends that ledger instead of recovering it, which would unhide the pad first.
    private volatile bool _retained;

    internal HidHideOwnedDeltaManager(
        IHidHideAdapter adapter,
        IHidHideOwnershipStore store)
    {
        _adapter = adapter;
        _store = store;
    }

    /// <summary>Makes WSGM able to read devices HidHide is hiding, before it needs to.</summary>
    /// <param name="controllerManagementEnabled">Whether controller management may run at all.</param>
    /// <param name="controllerReaderApplication">The WSGM image path to allow.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A description of what was found, for the log.</returns>
    /// <remarks>
    ///     <see cref="StartAsync" /> allowlists WSGM too, but only as the first step of WSGM's own
    ///     hiding transaction — which is to say only once WSGM already knows which devices to hide. That
    ///     ordering assumes WSGM is the only thing using HidHide. When something else hid the controller
    ///     first, the plugin cannot see the device it is being asked to discover, discovery finds
    ///     nothing, and the allowlisting that would have fixed it never runs because it comes later
    ///     (device evidence in <c>docs\device-integration.md</c>, "HidHide findings").
    ///     <para>
    ///         This adds nothing to the hidden set and takes nothing away from another owner: it only grants
    ///         WSGM's own process the ability to read. It is therefore safe before a transaction exists, and
    ///         it is idempotent, so the later transaction finds it present and records no delta.
    ///     </para>
    /// </remarks>
    internal async Task<string> EnsureReadableAsync(
        bool controllerManagementEnabled,
        string controllerReaderApplication,
        CancellationToken cancellationToken)
    {
        if (!controllerManagementEnabled || string.IsNullOrWhiteSpace(controllerReaderApplication))
        {
            return "Controller management is off; HidHide was not consulted.";
        }

        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _adapter.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Health is not HidHideHealthState.Ready)
            {
                return $"HidHide is not available ({snapshot.Health}); nothing to allow.";
            }

            if (!snapshot.Active || snapshot.Devices.Count == 0)
            {
                return "HidHide is hiding nothing; no allowance needed.";
            }

            if (Contains(snapshot.Applications, controllerReaderApplication))
            {
                return $"HidHide hides {snapshot.Devices.Count} device(s); WSGM is already allowed.";
            }

            var mutation = await _adapter.TryMutateAsync(
                snapshot,
                new HidHideEntryMutation(
                    HidHideMutationKind.Add,
                    HidHideEntryKind.Application,
                    controllerReaderApplication),
                cancellationToken).ConfigureAwait(false);
            if (!mutation.Applied)
            {
                return "HidHide is hiding devices and WSGM could not add itself to its allowlist: "
                       + mutation.Detail;
            }

            return $"HidHide hides {snapshot.Devices.Count} device(s) that WSGM does not own; "
                   + "added WSGM to its allowlist so the plugin can read them.";
        }
        finally
        {
            _transition.Release();
        }
    }

    internal async Task<HidHideActivationResult> StartAsync(
        string controllerReaderApplication,
        IReadOnlyList<PhysicalDeviceIdentity> physicalDevices,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerReaderApplication);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var retained = _retained;
            _retained = false;
            HidHideOwnershipLedger? carried = null;
            var existing = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null && retained)
            {
                // This session kept the pad hidden across a sleep or a recovery; carry the ledger on.
                carried = existing;
            }
            else if (existing is not null)
            {
                // A ledger loaded before this run writes anything records an interrupted ownership
                // transaction. Recover it before admitting a new transaction.
                var recovery = await CleanupUnderGateAsync(existing, cancellationToken)
                    .ConfigureAwait(false);
                if (!recovery.Verified)
                {
                    // Recovery could not put HidHide back, which is a real reason to keep hands off.
                    return new HidHideActivationResult(
                        false,
                        $"A previous HidHide ownership ledger could not be recovered: {recovery.Detail}");
                }

                Log.Info("Recovered an orphaned HidHide ownership ledger from a previous session.");
            }

            var snapshot = await _adapter.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Health is HidHideHealthState.Inactive)
            {
                // WSGM owns the cloak. Handheld Companion's uninstaller turns it off (its Inno
                // script runs HidHideCLI --cloak-off), and a WSGM that only checked the switch
                // then ran without a virtual pad while Steam read the physical one: Soft Pull
                // worked, Full Pull never fired (Xbox Ally X, 2026-09-28).
                var activated = await SetActiveAsync(true, cancellationToken).ConfigureAwait(false);
                if (!activated.Applied)
                {
                    return new HidHideActivationResult(false,
                        $"HidHide cloak could not be turned on: {activated.Detail}");
                }

                Log.Info("HidHide cloak was off; turned it on.");
                snapshot = activated.Current;
            }

            if (snapshot.Health is not HidHideHealthState.Ready || !snapshot.Active)
            {
                return new HidHideActivationResult(false,
                    $"HidHide prerequisite unavailable: {snapshot.Health} ({snapshot.Detail}).");
            }

            var ledger = carried ?? new HidHideOwnershipLedger();

            try
            {
                snapshot = await AddIfAbsentAsync(
                    snapshot,
                    ledger,
                    HidHideEntryKind.Application,
                    controllerReaderApplication,
                    cancellationToken).ConfigureAwait(false);

                // ReSharper disable once LoopCanBeConvertedToQuery
                foreach (var instancePath in physicalDevices
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

                if (!Contains(snapshot.Applications, controllerReaderApplication)
                    || physicalDevices.Where(device => device.RequiresHiding)
                        .Any(device => !Contains(snapshot.Devices, device.InstancePath)))
                {
                    throw new InvalidOperationException("HidHide readback did not contain every required entry.");
                }

                return new HidHideActivationResult(true, "WSGM-owned HidHide deltas applied and verified.");
            }
            catch (Exception ex)
            {
                ledger.RecoveryDetail = $"Activation failed: {ex.Message}";
                await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
                var cleanup = await CleanupUnderGateAsync(ledger, cancellationToken)
                    .ConfigureAwait(false);
                if (cleanup.Verified)
                {
                    cleanup = await DeactivateUnderGateAsync(cancellationToken).ConfigureAwait(false);
                }

                return new HidHideActivationResult(false,
                    cleanup.Verified
                        ? $"HidHide activation rolled back: {ex.Message}"
                        : $"HidHide activation cleanup is unverified: {ex.Message}");
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>Removes WSGM's entries and turns the cloak off, so the physical controller comes back.</summary>
    /// <param name="cancellationToken">Cancels the cleanup.</param>
    /// <returns>Verified only when every owned entry is gone and HidHide reads back inactive.</returns>
    /// <remarks>
    ///     Every exit runs this: normal shutdown, session end, the update and uninstall requests, and
    ///     make-safe. The cloak goes off whether or not this run turned it on, because WSGM owns it and a
    ///     user whose WSGM has closed must have their controller back, not a HidHide left cloaking.
    /// </remarks>
    internal async Task<HidHideCleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _retained = false;
            var ledger = await _store.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (ledger is not null)
            {
                var cleanup = await CleanupUnderGateAsync(ledger, cancellationToken).ConfigureAwait(false);
                if (!cleanup.Verified)
                {
                    return cleanup;
                }
            }

            return await DeactivateUnderGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>Keeps this session's entries and the cloak for the next start instead of recovering them.</summary>
    /// <remarks>Only for a controller WSGM takes again at once; <see cref="CleanupAsync" /> ends it.</remarks>
    internal void Retain()
    {
        _retained = true;
    }

    /// <summary>Uninstall: shows every device WSGM hid again and takes WSGM off the allowlist.</summary>
    /// <param name="ownApplications">WSGM's own executables, in any path notation.</param>
    /// <param name="cancellationToken">Cancels the cleanup.</param>
    /// <returns>Verified only when HidHide read back without any WSGM entry.</returns>
    /// <remarks>
    ///     Runs whether or not HidHide itself is about to be removed, because a HidHide that stays would
    ///     otherwise keep hiding the physical controller with no WSGM left to undo it. The allowlist
    ///     entries <see cref="EnsureReadableAsync" /> adds carry no ledger entry, so they are removed
    ///     by path here, and the cloak goes off last. An unverified result is never retried: the ledger
    ///     stays for the next attempt.
    /// </remarks>
    internal async Task<HidHideCleanupResult> CleanupForUninstallAsync(
        IReadOnlyList<string> ownApplications,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownApplications);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _adapter.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Health is HidHideHealthState.Unavailable)
            {
                return new HidHideCleanupResult(true, "HidHide is not installed, so it hides nothing.");
            }

            var ledger = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (ledger is not null)
            {
                var cleanup = await CleanupUnderGateAsync(ledger, cancellationToken).ConfigureAwait(false);
                if (!cleanup.Verified)
                {
                    return cleanup;
                }
            }

            foreach (var application in ownApplications.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                if (!await RemoveApplicationAsync(application, cancellationToken).ConfigureAwait(false))
                {
                    return new HidHideCleanupResult(false,
                        $"HidHide still allows {application} and did not accept its removal.");
                }
            }

            var deactivated = await DeactivateUnderGateAsync(cancellationToken).ConfigureAwait(false);
            return deactivated.Verified
                ? new HidHideCleanupResult(true, "Every WSGM entry is gone from HidHide and the cloak is off.")
                : deactivated;
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<bool> RemoveApplicationAsync(string application, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(application);
        for (var attempt = 0; attempt < MaximumCompareRetries; attempt++)
        {
            var snapshot = await _adapter.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.Health.IsWritable())
            {
                return false;
            }

            var stored = snapshot.Applications.FirstOrDefault(entry =>
                string.Equals(NormalizePath(entry), normalized, StringComparison.OrdinalIgnoreCase));
            if (stored is null)
            {
                return true;
            }

            await _adapter.TryMutateAsync(
                snapshot,
                new HidHideEntryMutation(HidHideMutationKind.Remove, HidHideEntryKind.Application, stored),
                cancellationToken).ConfigureAwait(false);
        }

        return false;
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
            State = HidHideOwnedDeltaState.Pending
        };
        ledger.Deltas.Add(delta);
        await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < MaximumCompareRetries; attempt++)
        {
            var result = await _adapter.TryMutateAsync(
                snapshot,
                new HidHideEntryMutation(HidHideMutationKind.Add, entryKind, value),
                cancellationToken).ConfigureAwait(false);
            snapshot = result.Current;
            if (result.Applied)
            {
                delta.State = HidHideOwnedDeltaState.Applied;
                await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
                return snapshot;
            }

            if (!Contains(Entries(snapshot, entryKind), value))
            {
                continue;
            }

            ledger.Deltas.Remove(delta);
            await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return snapshot;
        }

        throw new IOException($"HidHide {entryKind} entry kept changing during activation.");
    }

    private async Task<HidHideCleanupResult> CleanupUnderGateAsync(
        HidHideOwnershipLedger ledger,
        CancellationToken cancellationToken)
    {
        List<string> problems = [];
        foreach (var delta in ledger.Deltas.AsEnumerable().Reverse())
        {
            if (delta.State is HidHideOwnedDeltaState.Cleaned)
            {
                continue;
            }

            var cleaned = await RemoveOwnedDeltaAsync(delta, cancellationToken)
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
            return new HidHideCleanupResult(true, "Only WSGM-owned HidHide deltas were removed.");
        }

        ledger.RecoveryDetail = "Cleanup refused ambiguous entries: " + string.Join(", ", problems);
        await _store.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
        return new HidHideCleanupResult(false, ledger.RecoveryDetail);
    }

    private async Task<bool> RemoveOwnedDeltaAsync(
        HidHideOwnedDelta delta,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumCompareRetries; attempt++)
        {
            var snapshot = await _adapter.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!snapshot.Health.IsWritable())
            {
                return false;
            }

            var entries = Entries(snapshot, delta.EntryKind);
            var semanticCount = entries.Count(entry =>
                string.Equals(entry, delta.Value, StringComparison.OrdinalIgnoreCase));
            var exactCount = entries.Count(entry =>
                string.Equals(entry, delta.Value, StringComparison.Ordinal));

            if (semanticCount == 0)
            {
                return true;
            }

            if (semanticCount != 1 || exactCount != 1)
            {
                return false;
            }

            var result = await _adapter.TryMutateAsync(
                snapshot,
                new HidHideEntryMutation(HidHideMutationKind.Remove, delta.EntryKind, delta.Value),
                cancellationToken).ConfigureAwait(false);
            if (result.Applied)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Turns the cloak off when the driver answers; nothing to do when it is off or absent.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Verified when HidHide reads back inactive or is not installed.</returns>
    private async Task<HidHideCleanupResult> DeactivateUnderGateAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _adapter.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Health is HidHideHealthState.Unavailable)
        {
            return new HidHideCleanupResult(true, "HidHide is not installed, so it cloaks nothing.");
        }

        if (!snapshot.Active)
        {
            return new HidHideCleanupResult(true, "Only WSGM-owned HidHide deltas were removed; the cloak is off.");
        }

        var result = await SetActiveAsync(false, cancellationToken).ConfigureAwait(false);
        if (result.Applied)
        {
            Log.Info("HidHide cloak turned off; the physical controller is visible again.");
            return new HidHideCleanupResult(true, "Only WSGM-owned HidHide deltas were removed; the cloak is off.");
        }

        return new HidHideCleanupResult(false, $"HidHide cloak could not be turned off: {result.Detail}");
    }

    private async Task<HidHideMutationResult> SetActiveAsync(bool active, CancellationToken cancellationToken)
    {
        HidHideMutationResult result = new(false, await _adapter.ReadAsync(cancellationToken).ConfigureAwait(false),
            "HidHide was not written.");
        for (var attempt = 0; attempt < MaximumCompareRetries; attempt++)
        {
            var snapshot = result.Current;
            if (!snapshot.Health.IsWritable())
            {
                return new HidHideMutationResult(false, snapshot, $"HidHide is not writable: {snapshot.Detail}");
            }

            if (snapshot.Active == active)
            {
                return new HidHideMutationResult(true, snapshot, "HidHide is already in that state.");
            }

            result = await _adapter.TrySetActiveAsync(snapshot, active, cancellationToken).ConfigureAwait(false);
            if (result.Applied)
            {
                return result;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> Entries(
        HidHideExactSnapshot snapshot,
        HidHideEntryKind entryKind)
    {
        return entryKind is HidHideEntryKind.Application
            ? snapshot.Applications
            : snapshot.Devices;
    }

    /// <summary>Whether HidHide already lists this entry, in whichever notation it stored it.</summary>
    /// <param name="entries">Entries exactly as HidHide returned them.</param>
    /// <param name="value">The entry WSGM is looking for.</param>
    /// <returns>Whether it is present.</returns>
    /// <remarks>
    ///     A plain string compare is not enough for applications: HidHide stores them as NT device
    ///     paths — <c>\Device\HarddiskVolume3\Program Files\…</c> — while WSGM knows its own executables
    ///     by drive letter. Without normalization the allowlist grows on every activation and cleanup
    ///     leaves the other notation's duplicate behind (device evidence in
    ///     <c>docs\device-integration.md</c>, "HidHide findings").
    /// </remarks>
    internal static bool Contains(IEnumerable<string> entries, string value)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalized = NormalizePath(value);
        return entries.Any(entry =>
            string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizePath(entry), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reduces an entry to a form both notations agree on.</summary>
    /// <param name="value">A DOS path, an NT device path, or a device instance path.</param>
    /// <returns>The comparable form.</returns>
    /// <remarks>
    ///     Only the volume prefix differs between the two notations, so stripping it leaves the part
    ///     that identifies the file. Device instance paths carry no such prefix and pass through, which
    ///     is why the device list never had this problem.
    /// </remarks>
    internal static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var path = value.Trim().Replace('/', '\\');

        // \Device\HarddiskVolumeN\rest  ->  \rest
        const string devicePrefix = @"\device\harddiskvolume";
        if (path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var separator = path.IndexOf('\\', devicePrefix.Length);
            return separator < 0 ? string.Empty : path[separator..];
        }

        // C:\rest  ->  \rest. Deliberately only a drive letter: a UNC path has no volume to strip
        // and must keep its server and share, which are part of what identifies it.
        if (path is [_, ':', ..] && char.IsLetter(path[0]))
        {
            return path.Length == 2 ? string.Empty : path[2..];
        }

        return path;
    }
}

[JsonSerializable(typeof(HidHideOwnershipLedger))]
internal sealed partial class HidHideOwnershipJsonContext : JsonSerializerContext;
