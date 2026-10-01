using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Services;

namespace WSGM.Device.Msi.Claw;

/// <summary>Keeps only the original temporary state WSGM must restore after a crash.</summary>
/// <remarks>
///     Power and fans bind to the MSI_ACPI firmware identity and the controller mode to the MCU binding
///     (<see cref="ClawFirmwareIdentities" />). <see cref="Decide" /> holds the reconciliation policy.
/// </remarks>
internal sealed class ClawRecoveryJournal()
    : DeviceRecoveryJournal<ClawRecoveryState>(ClawRecoveryJsonContext.Default.RecoveryDocument)
{
    public static async ValueTask<ClawRecoveryJournal> OpenAsync(
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        var journal = new ClawRecoveryJournal();
        await journal.LoadAsync(stateDirectory, cancellationToken).ConfigureAwait(false);
        _ = await journal.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return journal;
    }

    /// <summary>Records what a journalled command left behind.</summary>
    /// <remarks>
    ///     A failed or unverified rollback stays outstanding with that status. An entry this command
    ///     opened is removed when the command was refused or its rollback was verified; otherwise it waits
    ///     for the service's release.
    /// </remarks>
    public async ValueTask CompleteCommandAsync(
        DeviceRecoveryOperation<ClawRecoveryState> operation,
        CapabilityCommandResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(result);
        var serviceId = operation.Entry.ServiceId;
        switch (result.Rollback)
        {
            case RollbackResult.RestoreFailed:
                await SetStatusAsync(serviceId, DeviceRecoveryStatus.RestoreFailed, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case RollbackResult.RestoredUnverified:
                await SetStatusAsync(serviceId, DeviceRecoveryStatus.RestoredUnverified, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case RollbackResult.NotRequired:
            case RollbackResult.RestoredVerified:
            default:
                break;
        }

        if (operation.Opened
            && (result.Outcome is CommandOutcome.Rejected
                || result.Rollback is RollbackResult.RestoredVerified))
        {
            await SetStatusAsync(serviceId, DeviceRecoveryStatus.RestoredVerified, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static ClawReconciliationAction Decide(
        DeviceRecoveryEntry<ClawRecoveryState> entry,
        string? currentFirmwareIdentity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (currentFirmwareIdentity is null)
        {
            // The service cannot be reached this cycle, so nothing is known about the firmware. The
            // entry waits for a cycle that can read it.
            return ClawReconciliationAction.ReportOnly;
        }

        var unknown = ClawFirmwareIdentities.IsUnknownEc(entry.FirmwareIdentity)
                      || ClawFirmwareIdentities.IsUnknownEc(currentFirmwareIdentity);
        if (!unknown && string.Equals(entry.FirmwareIdentity, currentFirmwareIdentity, StringComparison.Ordinal))
        {
            // One bounded reconciliation attempt is made per fresh device cycle. A transient bus
            // failure from the previous cycle must not permanently strand exact captured state,
            // but a firmware identity change still forbids the write.
            return ClawReconciliationAction.Restore;
        }

        if (entry.Status is DeviceRecoveryStatus.RestoreFailed)
        {
            return ClawReconciliationAction.Block;
        }

        // A different EC, or one that cannot be told apart from another, forbids the write. Keeping
        // the entry would fault the service on every start with nothing able to clear it, and an EC
        // update rewrites the state the entry captured anyway, so it is dropped.
        return ClawReconciliationAction.Discard;
    }

    protected override void ValidateEntry(DeviceRecoveryEntry<ClawRecoveryState> entry)
    {
        var expectedFirmware = entry.ServiceId switch
        {
            ServiceIds.Power or ServiceIds.Fans => ClawFirmwareIdentities.IsWmi(entry.FirmwareIdentity),
            ServiceIds.Controller => string.Equals(entry.FirmwareIdentity, ClawFirmwareIdentities.Mcu,
                StringComparison.Ordinal),
            _ => throw new InvalidDataException("A recovery entry names a non-restorable service.")
        };
        if (!expectedFirmware)
        {
            throw new InvalidDataException("A recovery entry has an unexpected firmware identity.");
        }

        var state = entry.OriginalState;
        var valid = entry.ServiceId switch
        {
            ServiceIds.Power => state.Kind is ClawRecoveryStateKind.Power
                                && state.SustainedWatts is >= byte.MinValue and <= byte.MaxValue
                                && state.BoostWatts is >= byte.MinValue and <= byte.MaxValue
                                && state.FastWatts is null or >= byte.MinValue and <= byte.MaxValue
                                && state.Scenario is not null,
            ServiceIds.Fans => state.Kind is ClawRecoveryStateKind.Fans
                               && state.LeftDuty.Length == 32
                               && state.LeftTemperature.Length == 32
                               && state.RightDuty.Length == 32
                               && state.RightTemperature.Length == 32
                               && state.CustomFlag is not null
                               && state.FullSpeedFlag is not null,
            _ => state.Kind is ClawRecoveryStateKind.ControllerMode
                 && state.ControllerMode is ClawControllerMode.XInput or ClawControllerMode.DirectInput
        };
        if (!valid)
        {
            throw new InvalidDataException($"Recovery state does not match service '{entry.ServiceId}'.");
        }
    }
}

internal enum ClawReconciliationAction
{
    Restore,
    ReportOnly,

    /// <summary>The firmware changed under the entry: drop it without restoring.</summary>
    Discard,
    Block
}

internal sealed record ClawRecoveryState
{
    public required ClawRecoveryStateKind Kind { get; init; }

    public int? SustainedWatts { get; init; }

    public int? BoostWatts { get; init; }

    public int? FastWatts { get; init; }

    public byte? Scenario { get; init; }

    public byte[] LeftDuty { get; init; } = [];

    public byte[] LeftTemperature { get; init; } = [];

    public byte[] RightDuty { get; init; } = [];

    public byte[] RightTemperature { get; init; } = [];

    public byte? CustomFlag { get; init; }

    public byte? FullSpeedFlag { get; init; }

    public ClawControllerMode? ControllerMode { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ClawRecoveryStateKind>))]
internal enum ClawRecoveryStateKind
{
    Power,
    Fans,
    ControllerMode
}

internal static class ClawRecoveryValues
{
    public static ClawRecoveryState Power(PowerPair snapshot)
    {
        return new ClawRecoveryState
        {
            Kind = ClawRecoveryStateKind.Power,
            SustainedWatts = snapshot.SustainedWatts,
            BoostWatts = snapshot.BoostWatts,
            FastWatts = snapshot.FastWatts,
            Scenario = snapshot.Scenario
        };
    }

    public static bool TryPower(ClawRecoveryState? value, out PowerPair? snapshot)
    {
        snapshot = null;
        if (value?.Kind is not ClawRecoveryStateKind.Power
            || value.SustainedWatts is not { } sustained
            || value.BoostWatts is not { } boost
            || value.Scenario is not { } scenario)
        {
            return false;
        }

        snapshot = new PowerPair(sustained, boost, scenario, value.FastWatts);
        return true;
    }

    public static ClawRecoveryState Fans(FanSnapshot snapshot)
    {
        return new ClawRecoveryState
        {
            Kind = ClawRecoveryStateKind.Fans,
            LeftDuty = [.. snapshot.Left.DutyBuffer],
            LeftTemperature = [.. snapshot.Left.TemperatureBuffer],
            RightDuty = [.. snapshot.Right.DutyBuffer],
            RightTemperature = [.. snapshot.Right.TemperatureBuffer],
            CustomFlag = snapshot.CustomFlag,
            FullSpeedFlag = snapshot.FullSpeedFlag
        };
    }

    public static bool TryFans(ClawRecoveryState? value, out FanSnapshot? snapshot)
    {
        snapshot = null;
        if (value?.Kind is not ClawRecoveryStateKind.Fans
            || value.LeftDuty.Length != 32
            || value.LeftTemperature.Length != 32
            || value.RightDuty.Length != 32
            || value.RightTemperature.Length != 32
            || value.CustomFlag is not { } customFlag
            || value.FullSpeedFlag is not { } fullSpeedFlag)
        {
            return false;
        }

        snapshot = new FanSnapshot(
            new FanTable([.. value.LeftDuty], [.. value.LeftTemperature]),
            new FanTable([.. value.RightDuty], [.. value.RightTemperature]),
            customFlag,
            fullSpeedFlag);
        return true;
    }

    public static ClawRecoveryState ControllerMode(ClawControllerMode mode)
    {
        return new ClawRecoveryState
        {
            Kind = ClawRecoveryStateKind.ControllerMode,
            ControllerMode = mode
        };
    }

    public static bool TryControllerMode(ClawRecoveryState? value, out ClawControllerMode mode)
    {
        mode = value?.ControllerMode ?? ClawControllerMode.Offline;
        return value?.Kind is ClawRecoveryStateKind.ControllerMode
               && mode is ClawControllerMode.XInput or ClawControllerMode.DirectInput;
    }
}

// Unknown members are ignored: a record a different build of this package left behind must still
// load, or the services it covers would stay blocked with nothing in the product able to clear it.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceRecoveryDocument<ClawRecoveryState>), TypeInfoPropertyName = "RecoveryDocument")]
internal sealed partial class ClawRecoveryJsonContext : JsonSerializerContext;
