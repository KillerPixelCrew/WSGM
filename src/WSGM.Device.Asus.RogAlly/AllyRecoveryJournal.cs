// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Asus.RogAlly;

/// <summary>Keeps the original temporary state WSGM must restore after a crash.</summary>
/// <remarks>
///     Narrowed to what the Ally changes temporarily: the power limits and mode, the fan curves, and the
///     controller configuration. Charge limit and lighting are persistent user choices and are never
///     journalled or reverted.
/// </remarks>
internal sealed class AllyRecoveryJournal()
    : DeviceRecoveryJournal<AllyRecoveryState>(AllyRecoveryJsonContext.Default.RecoveryDocument)
{
    public static async ValueTask<AllyRecoveryJournal> OpenAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        var journal = new AllyRecoveryJournal();
        await journal.LoadAsync(stateDirectory, cancellationToken).ConfigureAwait(false);
        return journal;
    }

    /// <summary>Records the state captured before a service's first mutation in this cycle.</summary>
    /// <remarks>
    ///     An entry whose restore was unverified or failed keeps its original state and is set pending
    ///     again: the explicit command that called this is the user action that allows the next release to
    ///     write that original once more. Nothing re-arms it automatically.
    /// </remarks>
    public async ValueTask ArmAsync(
        string serviceId,
        string firmwareIdentity,
        AllyRecoveryState originalState,
        CancellationToken cancellationToken)
    {
        if (EntryFor(serviceId) is { Status: not DeviceRecoveryStatus.Pending })
        {
            await SetStatusAsync(serviceId, DeviceRecoveryStatus.Pending, cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = await BeginAsync(serviceId, firmwareIdentity, originalState, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The original a release may write back: only a pending entry, never an unresolved one.</summary>
    public AllyRecoveryState? PendingOriginalFor(string serviceId)
    {
        return EntryFor(serviceId) is { Status: DeviceRecoveryStatus.Pending } entry ? entry.OriginalState : null;
    }

    /// <summary>Whether an outstanding entry may be restored on this firmware.</summary>
    internal static AllyReconciliationAction Decide(
        DeviceRecoveryEntry<AllyRecoveryState> entry,
        string? currentFirmwareIdentity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // A restore already reached the device but did not read back. Another automatic write would
        // retry an uncertain cleanup; the entry waits for an explicit command (see ArmAsync).
        if (entry.Status is DeviceRecoveryStatus.RestoredUnverified or DeviceRecoveryStatus.RestoreFailed)
        {
            return AllyReconciliationAction.Block;
        }

        if (string.Equals(entry.FirmwareIdentity, currentFirmwareIdentity, StringComparison.Ordinal))
        {
            return AllyReconciliationAction.Restore;
        }

        return AllyReconciliationAction.ReportOnly;
    }

    /// <summary>Whether a captured limit is one the recovery record can hold.</summary>
    internal static bool IsValidWatts(int? value)
    {
        return value is >= 1 and <= 80;
    }

    protected override void ValidateEntry(DeviceRecoveryEntry<AllyRecoveryState> entry)
    {
        var state = entry.OriginalState;
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

// Unknown members are ignored: a record a different build of this package left behind must still
// load, or the services it covers would stay blocked with nothing in the product able to clear it.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceRecoveryDocument<AllyRecoveryState>), TypeInfoPropertyName = "RecoveryDocument")]
internal sealed partial class AllyRecoveryJsonContext : JsonSerializerContext;
