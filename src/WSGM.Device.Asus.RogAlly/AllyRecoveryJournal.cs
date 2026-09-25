// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Device.Asus.RogAlly;

/// <summary>Keeps the original temporary state WSGM must restore after a crash.</summary>
/// <remarks>
///     The Claw reference plugin's journal, narrowed to what the Ally changes temporarily: the power
///     limits and mode, the fan curves, and the controller configuration. Charge limit and lighting are
///     persistent user choices and are never journalled or reverted.
/// </remarks>
internal sealed class AllyRecoveryJournal : IAsyncDisposable
{
    private const int CurrentVersion = 1;
    private const int MaxBytes = 16 * 1024;
    private const string FileName = "temporary-state.v1.json";
    private readonly string? _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private List<AllyRecoveryEntry> _entries = [];

    private AllyRecoveryJournal(string? path, CapabilityReason? failureReason)
    {
        _path = path;
        FailureReason = failureReason;
    }

    public CapabilityReason? FailureReason { get; private set; }

    public IReadOnlyList<AllyRecoveryEntry> OutstandingEntries => [.. _entries];

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    public static async ValueTask<AllyRecoveryJournal> OpenAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            return Failed("WSGM did not provide a writable plugin state directory.");
        }

        string path;
        try
        {
            var root = Path.GetFullPath(stateDirectory);
            Directory.CreateDirectory(root);
            path = Path.Combine(root, FileName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Failed($"The plugin recovery directory is unavailable ({ex.GetType().Name}).");
        }

        var journal = new AllyRecoveryJournal(path, null);
        await journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        return journal;
    }

    /// <summary>Records the state captured before a service's first mutation in this cycle.</summary>
    /// <remarks>
    ///     An entry whose restore was unverified or failed keeps its original state and is set pending
    ///     again: the explicit command that called this is the user action that allows the next release to
    ///     write that original once more. Nothing re-arms it automatically.
    /// </remarks>
    /// <returns>True when a new entry was written; false when one was already outstanding.</returns>
    public async ValueTask<bool> BeginAsync(
        string serviceId,
        string firmwareIdentity,
        AllyRecoveryState originalState,
        CancellationToken cancellationToken)
    {
        var entry = new AllyRecoveryEntry
        {
            ServiceId = serviceId,
            FirmwareIdentity = firmwareIdentity,
            OriginalState = originalState,
            Status = AllyRecoveryStatus.Pending
        };
        ValidateEntry(entry);
        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _entries.SingleOrDefault(candidate => candidate.ServiceId == serviceId);
            if (existing is not null)
            {
                if (existing.Status is not AllyRecoveryStatus.Pending)
                {
                    await SaveAsync(
                    [
                        .. _entries.Select(candidate => candidate.ServiceId == serviceId
                            ? candidate with { Status = AllyRecoveryStatus.Pending }
                            : candidate)
                    ], cancellationToken).ConfigureAwait(false);
                }

                return false;
            }

            await SaveAsync([.. _entries, entry], cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public bool HasUnrestoredMutation(string serviceId)
    {
        return _entries.Any(entry => entry.ServiceId == serviceId);
    }

    public AllyRecoveryState? OriginalStateFor(string serviceId)
    {
        return _entries.SingleOrDefault(entry => entry.ServiceId == serviceId)?.OriginalState;
    }

    /// <summary>The original a release may write back: only a pending entry, never an unresolved one.</summary>
    public AllyRecoveryState? PendingOriginalFor(string serviceId)
    {
        return _entries.SingleOrDefault(entry => entry.ServiceId == serviceId
                                                 && entry.Status is AllyRecoveryStatus.Pending)?.OriginalState;
    }

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
            FailureReason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                $"The plugin recovery record is not writable ({ex.GetType().Name}).");
        }
        finally
        {
            _writeGate.Release();
        }

        return FailureReason;
    }

    /// <summary>Removes, keeps or marks a service's entry after the service restored its state.</summary>
    public async ValueTask CompleteAsync(
        string serviceId,
        AllyRecoveryStatus status,
        CancellationToken cancellationToken)
    {
        if (status is AllyRecoveryStatus.Pending)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<AllyRecoveryEntry> entries = [.. _entries];
            var index = entries.FindIndex(entry => entry.ServiceId == serviceId);
            if (index < 0)
            {
                return;
            }

            if (status is AllyRecoveryStatus.RestoredVerified)
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

    /// <summary>Whether an outstanding entry may be restored on this firmware.</summary>
    internal static AllyReconciliationAction Decide(AllyRecoveryEntry entry, string? currentFirmwareIdentity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // A restore already reached the device but did not read back. Another automatic write would
        // retry an uncertain cleanup; the entry waits for an explicit command (see BeginAsync).
        if (entry.Status is AllyRecoveryStatus.RestoredUnverified or AllyRecoveryStatus.RestoreFailed)
        {
            return AllyReconciliationAction.Block;
        }

        if (string.Equals(entry.FirmwareIdentity, currentFirmwareIdentity, StringComparison.Ordinal))
        {
            return AllyReconciliationAction.Restore;
        }

        return AllyReconciliationAction.ReportOnly;
    }

    private static AllyRecoveryJournal Failed(string detail)
    {
        return new AllyRecoveryJournal(null, new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
    }

    private async ValueTask LoadAsync(CancellationToken cancellationToken)
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            await using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaxBytes)
            {
                throw new InvalidDataException("The Ally recovery record exceeds 16 KiB.");
            }

            var document = await JsonSerializer.DeserializeAsync(stream,
                                   AllyRecoveryJsonContext.Default.AllyRecoveryDocument, cancellationToken)
                               .ConfigureAwait(false)
                           ?? throw new InvalidDataException("The Ally recovery record was empty.");
            ValidateDocument(document);
            _entries = [.. document.Entries];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailureReason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                $"The plugin recovery record is unavailable or invalid ({ex.GetType().Name}).");
            _entries = [];
        }
    }

    private async ValueTask SaveAsync(List<AllyRecoveryEntry> entries, CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            throw new InvalidOperationException("The plugin recovery path is unavailable.");
        }

        var document = new AllyRecoveryDocument
        {
            Version = CurrentVersion,
            Entries = [.. entries.OrderBy(entry => entry.ServiceId, StringComparer.Ordinal)]
        };
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, AllyRecoveryJsonContext.Default.AllyRecoveryDocument);
        if (bytes.Length > MaxBytes)
        {
            throw new InvalidDataException("The Ally recovery record exceeds 16 KiB.");
        }

        var temporary = _path + ".tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
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
            throw new InvalidOperationException(FailureReason?.Detail ?? "The plugin recovery record is unavailable.");
        }
    }

    private static void ValidateDocument(AllyRecoveryDocument document)
    {
        if (document.Version != CurrentVersion || document.Entries.Count > 3)
        {
            throw new InvalidDataException("The Ally recovery record header is invalid.");
        }

        HashSet<string> services = new(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            ValidateEntry(entry);
            if (!services.Add(entry.ServiceId))
            {
                throw new InvalidDataException("The Ally recovery record has duplicate services.");
            }
        }
    }

    private static void ValidateEntry(AllyRecoveryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.FirmwareIdentity) || entry.FirmwareIdentity.Length > 128)
        {
            throw new InvalidDataException("An Ally recovery entry has no firmware identity.");
        }

        var state = entry.OriginalState ?? throw new InvalidDataException("An Ally recovery entry has no state.");
        var valid = entry.ServiceId switch
        {
            AllyServiceIds.Power => state.Kind is AllyRecoveryStateKind.Power
                                    && state.Mode is null or >= 0 and <= 2
                                    && IsValidWatts(state.Sustained) && IsValidWatts(state.Slow)
                                    && IsValidWatts(state.Fast),
            AllyServiceIds.Fans => state.Kind is AllyRecoveryStateKind.Fans
                                   && AsusAcpiProtocol.IsValidCurve(state.CpuCurve)
                                   && AsusAcpiProtocol.IsValidCurve(state.GpuCurve)
                                   && (state.MidCurve.Length == 0 || AsusAcpiProtocol.IsValidCurve(state.MidCurve)),
            AllyServiceIds.Controller => state.Kind is AllyRecoveryStateKind.ControllerConfiguration,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException($"Recovery state does not match service '{entry.ServiceId}'.");
        }
    }

    /// <summary>Whether a captured limit is one the recovery record can hold.</summary>
    internal static bool IsValidWatts(int? value)
    {
        return value is >= 1 and <= 80;
    }
}

internal sealed record AllyRecoveryDocument
{
    public required int Version { get; init; }

    public IReadOnlyList<AllyRecoveryEntry> Entries { get; init; } = [];
}

internal sealed record AllyRecoveryEntry
{
    public required string ServiceId { get; init; }

    public required string FirmwareIdentity { get; init; }

    public required AllyRecoveryState OriginalState { get; init; }

    public required AllyRecoveryStatus Status { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<AllyRecoveryStatus>))]
internal enum AllyRecoveryStatus
{
    Pending,
    RestoredVerified,
    RestoredUnverified,
    RestoreFailed
}

internal enum AllyReconciliationAction
{
    Restore,
    ReportOnly,
    Block
}

[JsonConverter(typeof(JsonStringEnumConverter<AllyRecoveryStateKind>))]
internal enum AllyRecoveryStateKind
{
    Power,
    Fans,

    /// <summary>The MCU button tables. There is no readable original; restoring writes the defaults.</summary>
    ControllerConfiguration
}

internal sealed record AllyRecoveryState
{
    public required AllyRecoveryStateKind Kind { get; init; }

    public int? Sustained { get; init; }

    public int? Slow { get; init; }

    public int? Fast { get; init; }

    public int? Mode { get; init; }

    public byte[] CpuCurve { get; init; } = [];

    public byte[] GpuCurve { get; init; } = [];

    public byte[] MidCurve { get; init; } = [];

    public static AllyRecoveryState Power(AllyPowerState state)
    {
        return new AllyRecoveryState
        {
            Kind = AllyRecoveryStateKind.Power,
            Sustained = state.Sustained,
            Slow = state.Slow,
            Fast = state.Fast,
            Mode = state.Mode
        };
    }

    public static AllyRecoveryState Fans(AllyFanSnapshot snapshot)
    {
        return new AllyRecoveryState
        {
            Kind = AllyRecoveryStateKind.Fans,
            CpuCurve = [.. snapshot.Cpu ?? []],
            GpuCurve = [.. snapshot.Gpu ?? []],
            MidCurve = [.. snapshot.Mid ?? []],
            Mode = snapshot.Mode
        };
    }

    public static AllyRecoveryState Controller()
    {
        return new AllyRecoveryState { Kind = AllyRecoveryStateKind.ControllerConfiguration };
    }

    public AllyPowerState? ToPower()
    {
        return Kind is AllyRecoveryStateKind.Power ? new AllyPowerState(Sustained, Slow, Fast, Mode) : null;
    }

    public AllyFanSnapshot? ToFans()
    {
        return Kind is AllyRecoveryStateKind.Fans
            ? new AllyFanSnapshot([.. CpuCurve], [.. GpuCurve], MidCurve.Length == 0 ? null : [.. MidCurve], Mode)
            : null;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AllyRecoveryDocument))]
internal sealed partial class AllyRecoveryJsonContext : JsonSerializerContext;
