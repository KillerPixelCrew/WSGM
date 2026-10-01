using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Sdk.Services;

/// <summary>Keeps the original temporary state a plugin must restore after a crash, one entry per service.</summary>
/// <typeparam name="TState">The package's own record of a captured original.</typeparam>
/// <remarks>
///     The record is <c>temporary-state.v1.json</c> in the plugin state directory WSGM provides, replaced
///     atomically on every change. A package derives one journal, supplies the source-generated type
///     information for its state, validates its entries, and owns the policy for when an entry is restored.
///     A record another build of the package wrote loads as long as its entries validate, so the package's
///     JSON context should ignore members its state type does not declare.
///     A record that cannot be read or written leaves <see cref="FailureReason" /> set and refuses every
///     change, so a service that needs it can be blocked rather than mutate without a restore point.
/// </remarks>
public abstract class DeviceRecoveryJournal<TState> : IAsyncDisposable
    where TState : class
{
    private const int CurrentVersion = 1;
    private const string FileName = "temporary-state.v1.json";
    private readonly JsonTypeInfo<DeviceRecoveryDocument<TState>> _typeInfo;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private List<DeviceRecoveryEntry<TState>> _entries = [];
    private string? _path;

    /// <summary>Creates a journal that serializes its record with <paramref name="typeInfo" />.</summary>
    /// <param name="typeInfo">Source-generated type information for the package's document.</param>
    protected DeviceRecoveryJournal(JsonTypeInfo<DeviceRecoveryDocument<TState>> typeInfo)
    {
        _typeInfo = typeInfo ?? throw new ArgumentNullException(nameof(typeInfo));
    }

    /// <summary>Why the record is unavailable, or null while it is readable and writable.</summary>
    public CapabilityReason? FailureReason { get; private set; }

    /// <summary>Every entry not yet restored.</summary>
    public IReadOnlyList<DeviceRecoveryEntry<TState>> OutstandingEntries => [.. _entries];

    /// <summary>The record's state as plugin diagnostics report it: <c>blocked</c>, <c>pending</c> or <c>healthy</c>.</summary>
    public string DiagnosticState => FailureReason is not null ? "blocked" : _entries.Count == 0 ? "healthy" : "pending";

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>The outstanding entry of a service, if it has one.</summary>
    /// <param name="serviceId">The service.</param>
    /// <returns>The entry, or null.</returns>
    public DeviceRecoveryEntry<TState>? EntryFor(string serviceId)
    {
        return _entries.SingleOrDefault(entry => string.Equals(entry.ServiceId, serviceId, StringComparison.Ordinal));
    }

    /// <summary>Whether a service has an outstanding entry.</summary>
    /// <param name="serviceId">The service.</param>
    /// <returns>True while an entry is outstanding.</returns>
    public bool HasUnrestoredMutation(string serviceId)
    {
        return EntryFor(serviceId) is not null;
    }

    /// <summary>The state captured immediately before a service's first mutation.</summary>
    /// <param name="serviceId">The service whose outstanding mutation is being restored.</param>
    /// <returns>The captured state, or null when the service has no outstanding mutation.</returns>
    public TState? OriginalStateFor(string serviceId)
    {
        return EntryFor(serviceId)?.OriginalState;
    }

    /// <summary>Records the state captured before a service's first mutation.</summary>
    /// <param name="serviceId">The service about to mutate.</param>
    /// <param name="firmwareIdentity">The firmware the captured state belongs to.</param>
    /// <param name="originalState">The state to restore.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    ///     The new entry, opened; or a pending entry already outstanding, kept with its first original and
    ///     not opened.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     The record is unavailable, or the service's entry holds an unresolved restore.
    /// </exception>
    public async ValueTask<DeviceRecoveryOperation<TState>> BeginAsync(
        string serviceId,
        string firmwareIdentity,
        TState originalState,
        CancellationToken cancellationToken)
    {
        var entry = new DeviceRecoveryEntry<TState>
        {
            ServiceId = serviceId,
            FirmwareIdentity = firmwareIdentity,
            OriginalState = originalState,
            Status = DeviceRecoveryStatus.Pending
        };
        Validate(entry);
        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (EntryFor(serviceId) is { } existing)
            {
                if (existing.Status is DeviceRecoveryStatus.RestoredUnverified or DeviceRecoveryStatus.RestoreFailed)
                {
                    throw new InvalidOperationException($"Recovery for service '{serviceId}' is unresolved.");
                }

                return new DeviceRecoveryOperation<TState>(existing, false);
            }

            await SaveAsync([.. _entries, entry], cancellationToken).ConfigureAwait(false);
            return new DeviceRecoveryOperation<TState>(entry, true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Records what became of a service's entry.</summary>
    /// <param name="serviceId">The service.</param>
    /// <param name="status">
    ///     <see cref="DeviceRecoveryStatus.RestoredVerified" /> removes the entry; any other status is kept
    ///     with it. A service without an entry is left alone.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task completing once the record is written.</returns>
    public async ValueTask SetStatusAsync(
        string serviceId,
        DeviceRecoveryStatus status,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DeviceRecoveryEntry<TState>> entries = [.. _entries];
            var index = entries.FindIndex(entry => string.Equals(entry.ServiceId, serviceId, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            if (status is DeviceRecoveryStatus.RestoredVerified)
            {
                entries.RemoveAt(index);
            }
            else
            {
                entries[index] = entries[index] with { Status = status };
            }

            await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Writes the record again to prove it is still writable.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="FailureReason" /> after the check.</returns>
    public async ValueTask<CapabilityReason?> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (FailureReason is not null)
        {
            return FailureReason;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAsync([.. _entries], cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailureReason = new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                $"The plugin recovery record is not writable ({ex.GetType().Name}).");
        }
        finally
        {
            _writeGate.Release();
        }

        return FailureReason;
    }

    /// <summary>Opens the record in the plugin state directory and loads its entries.</summary>
    /// <param name="stateDirectory">The plugin state directory WSGM provided.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task completing once the entries are loaded or <see cref="FailureReason" /> is set.</returns>
    protected async ValueTask LoadAsync(string stateDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            FailureReason = Unavailable("WSGM did not provide a writable plugin state directory.");
            return;
        }

        try
        {
            var root = Path.GetFullPath(stateDirectory);
            Directory.CreateDirectory(root);
            _path = Path.Combine(root, FileName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailureReason = Unavailable($"The plugin recovery directory is unavailable ({ex.GetType().Name}).");
            return;
        }

        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            await using FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync(stream, _typeInfo, cancellationToken)
                               .ConfigureAwait(false)
                           ?? throw new InvalidDataException("The recovery record was empty.");
            ValidateDocument(document);
            _entries = [.. document.Entries];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailureReason = Unavailable($"The plugin recovery record is unavailable or invalid ({ex.GetType().Name}).");
            _entries = [];
        }
    }

    /// <summary>Checks that an entry's service, firmware identity and state belong together.</summary>
    /// <param name="entry">The entry being recorded or loaded.</param>
    /// <exception cref="InvalidDataException">The entry is not one this package could have written.</exception>
    protected abstract void ValidateEntry(DeviceRecoveryEntry<TState> entry);

    private static CapabilityReason Unavailable(string detail)
    {
        return new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail);
    }

    private async ValueTask SaveAsync(List<DeviceRecoveryEntry<TState>> entries, CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            throw new InvalidOperationException("The plugin recovery path is unavailable.");
        }

        var document = new DeviceRecoveryDocument<TState>
        {
            Version = CurrentVersion,
            Entries = [.. entries.OrderBy(entry => entry.ServiceId, StringComparer.Ordinal)]
        };
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, _typeInfo);
        var temporary = _path + ".tmp";
        try
        {
            await using (FileStream stream = new(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            File.Move(temporary, _path, true);
            _entries = entries;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        if (FailureReason is not null || _path is null)
        {
            throw new InvalidOperationException(
                FailureReason?.Detail ?? "The plugin recovery record is unavailable.");
        }
    }

    private void ValidateDocument(DeviceRecoveryDocument<TState> document)
    {
        if (document.Version != CurrentVersion)
        {
            throw new InvalidDataException("The recovery record header is invalid.");
        }

        HashSet<string> services = new(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            Validate(entry);
            if (!services.Add(entry.ServiceId))
            {
                throw new InvalidDataException("The recovery record has duplicate services.");
            }
        }
    }

    private void Validate(DeviceRecoveryEntry<TState> entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.ServiceId)
            || string.IsNullOrWhiteSpace(entry.FirmwareIdentity)
            || entry.OriginalState is null)
        {
            throw new InvalidDataException("A recovery entry is incomplete.");
        }

        ValidateEntry(entry);
    }
}

/// <summary>The recovery record as it is stored.</summary>
/// <typeparam name="TState">The package's own record of a captured original.</typeparam>
public sealed record DeviceRecoveryDocument<TState>
    where TState : class
{
    /// <summary>The record format version.</summary>
    public required int Version { get; init; }

    /// <summary>Every outstanding entry, ordered by service.</summary>
    public IReadOnlyList<DeviceRecoveryEntry<TState>> Entries { get; init; } = [];
}

/// <summary>One service's captured original and what became of its restore.</summary>
/// <typeparam name="TState">The package's own record of a captured original.</typeparam>
public sealed record DeviceRecoveryEntry<TState>
    where TState : class
{
    /// <summary>The service that mutated.</summary>
    public required string ServiceId { get; init; }

    /// <summary>The firmware the captured state belongs to.</summary>
    /// <remarks>Whether an entry is restored on other firmware is the package's decision.</remarks>
    public required string FirmwareIdentity { get; init; }

    /// <summary>The state captured immediately before the first mutation.</summary>
    public required TState OriginalState { get; init; }

    /// <summary>What became of the restore.</summary>
    public required DeviceRecoveryStatus Status { get; init; }
}

/// <summary>The entry a mutation runs under.</summary>
/// <param name="Entry">The outstanding entry.</param>
/// <param name="Opened">Whether this call created it; false when an earlier mutation's entry was kept.</param>
/// <typeparam name="TState">The package's own record of a captured original.</typeparam>
public sealed record DeviceRecoveryOperation<TState>(DeviceRecoveryEntry<TState> Entry, bool Opened)
    where TState : class;

/// <summary>What became of a recovery entry's restore.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceRecoveryStatus>))]
public enum DeviceRecoveryStatus
{
    /// <summary>The mutation is outstanding and the original has not been written back.</summary>
    Pending,

    /// <summary>The original was written back; the entry is removed.</summary>
    RestoredVerified,

    /// <summary>The original was written back but could not be confirmed.</summary>
    RestoredUnverified,

    /// <summary>Writing the original back failed.</summary>
    RestoreFailed
}
