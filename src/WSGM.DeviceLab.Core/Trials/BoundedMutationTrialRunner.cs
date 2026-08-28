using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.DeviceLab.Core.Catalog;

namespace WSGM.DeviceLab.Core.Trials;

/// <summary>Exact two-value power state used by the temporary pair trial.</summary>
public readonly record struct TrialPowerPair(int SustainedWatts, int BoostWatts);

/// <summary>Typed transport for the one temporary power-pair trial.</summary>
public interface IPowerPairTrialTransport
{
    /// <summary>Reads both current values atomically enough for exact restoration.</summary>
    ValueTask<TrialPowerPair> ReadPairAsync(CancellationToken cancellationToken);

    /// <summary>Writes one reviewed temporary pair.</summary>
    ValueTask WritePairAsync(TrialPowerPair pair, CancellationToken cancellationToken);
}

/// <summary>One channel's current fan and firmware-ownership state.</summary>
public readonly record struct TrialFanState(int Channel, int DutyPercent, bool FirmwareControlled, int? Rpm);

/// <summary>Typed transport for the one-fan safe-duty trial.</summary>
public interface IFanDutyTrialTransport
{
    /// <summary>Reads current duty, tachometer, and firmware-control state.</summary>
    ValueTask<TrialFanState> ReadStateAsync(CancellationToken cancellationToken);

    /// <summary>Applies one current-or-higher reviewed duty to the same channel.</summary>
    ValueTask SetDutyAsync(int channel, int dutyPercent, CancellationToken cancellationToken);

    /// <summary>Restores exact duty and firmware ownership.</summary>
    ValueTask RestoreAsync(TrialFanState state, CancellationToken cancellationToken);

    /// <summary>Independent emergency action that yields control to firmware.</summary>
    ValueTask ReleaseToFirmwareAsync(CancellationToken cancellationToken);
}

/// <summary>Typed transport for the low-amplitude rumble trial.</summary>
public interface IRumbleTrialTransport
{
    /// <summary>Reads whether output is currently guaranteed zero.</summary>
    ValueTask<bool> IsZeroAsync(CancellationToken cancellationToken);

    /// <summary>Applies the compiled low-amplitude pulse.</summary>
    ValueTask ApplyLowPulseAsync(CancellationToken cancellationToken);

    /// <summary>Independently observes the physical or transport-level pulse acknowledgement.</summary>
    ValueTask<bool> ObservePulseAsync(CancellationToken cancellationToken);

    /// <summary>Uses the independent emergency zero-output path.</summary>
    ValueTask ZeroOutputAsync(CancellationToken cancellationToken);
}

/// <summary>One volatile RGB zone state.</summary>
public readonly record struct TrialRgbState(int Zone, int PackedRgb, int BrightnessPercent);

/// <summary>Typed transport for a profile already proven device-volatile.</summary>
public interface IVolatileRgbTrialTransport
{
    /// <summary>Whether the exact target profile is independently proven volatile.</summary>
    bool IsProvenVolatile { get; }

    /// <summary>Reads one compiled zone's exact state.</summary>
    ValueTask<TrialRgbState> ReadZoneAsync(CancellationToken cancellationToken);

    /// <summary>Applies one compiled low-brightness color to that zone.</summary>
    ValueTask ApplyLowBrightnessAsync(int zone, CancellationToken cancellationToken);

    /// <summary>Restores the exact original zone state.</summary>
    ValueTask RestoreZoneAsync(TrialRgbState state, CancellationToken cancellationToken);
}

/// <summary>Controller state pinned to physical USB location across re-enumeration.</summary>
public readonly record struct TrialControllerModeState(string Mode, string ProductId, string PhysicalLocation);

/// <summary>Typed transport for a single controller-mode continuation trial.</summary>
public interface IControllerModeTrialTransport
{
    /// <summary>Reads current mode, PID, and physical USB location.</summary>
    ValueTask<TrialControllerModeState> ReadStateAsync(CancellationToken cancellationToken);

    /// <summary>Switches once to the compiled alternate mode.</summary>
    ValueTask SwitchToAlternateAsync(CancellationToken cancellationToken);

    /// <summary>Waits for the alternate PID at the same physical USB location.</summary>
    ValueTask<TrialControllerModeState> WaitForAlternateAtLocationAsync(
        string physicalLocation,
        CancellationToken cancellationToken);

    /// <summary>Restores the compiled original mode.</summary>
    ValueTask RestoreModeAsync(string originalMode, CancellationToken cancellationToken);

    /// <summary>Waits for the original PID at the same physical USB location.</summary>
    ValueTask<TrialControllerModeState> WaitForOriginalAtLocationAsync(
        string physicalLocation,
        string originalProductId,
        CancellationToken cancellationToken);
}

/// <summary>Context common to one authorized, journalled trial transaction.</summary>
public sealed record MutationTrialExecutionContext
{
    /// <summary>Reviewed local metadata.</summary>
    public required MutationTrialMetadata Metadata { get; init; }

    /// <summary>Short-lived state-pinned authorization.</summary>
    public required MutationTrialAuthorization Authorization { get; init; }

    /// <summary>Fresh authorization snapshot immediately before execution.</summary>
    public required MutationTrialAuthorizationSnapshot Snapshot { get; init; }

    /// <summary>New append-only session journal.</summary>
    public required MutationTrialJournal Journal { get; init; }
}

/// <summary>Executes the five closed mutation families with mandatory rollback and readback.</summary>
public static class BoundedMutationTrialRunner
{
    /// <summary>Runs one temporary pair step and restores the exact original pair.</summary>
    public static Task<MutationTrialOutcome> RunPowerPairAsync(
        MutationTrialExecutionContext context,
        IPowerPairTrialTransport transport,
        TrialPowerPair planned,
        CancellationToken cancellationToken) => RunAsync(
            context,
            MutationTrialFamily.TemporaryPowerPair,
            read: transport.ReadPairAsync,
            planned: _ => planned,
            apply: transport.WritePairAsync,
            observeApplied: async (value, token) => await transport.ReadPairAsync(token).ConfigureAwait(false) == value,
            restore: transport.WritePairAsync,
            emergency: transport.WritePairAsync,
            encode: pair => new CapabilityValue
            {
                Kind = CapabilityValueKind.Curve,
                CurveValue = [new CurvePoint(0, pair.SustainedWatts), new CurvePoint(1, pair.BoostWatts)],
            },
            cancellationToken);

    /// <summary>Runs one current-or-higher fan step and returns ownership to its original mode.</summary>
    public static Task<MutationTrialOutcome> RunFanAsync(
        MutationTrialExecutionContext context,
        IFanDutyTrialTransport transport,
        int plannedDutyPercent,
        CancellationToken cancellationToken) => RunAsync(
            context,
            MutationTrialFamily.FanDuty,
            read: transport.ReadStateAsync,
            planned: original => original with { DutyPercent = Math.Max(original.DutyPercent, plannedDutyPercent) },
            apply: (state, token) => transport.SetDutyAsync(state.Channel, state.DutyPercent, token),
            observeApplied: async (value, token) =>
            {
                TrialFanState observed = await transport.ReadStateAsync(token).ConfigureAwait(false);
                return observed.Channel == value.Channel && observed.DutyPercent == value.DutyPercent
                    && observed.Rpm is not null;
            },
            restore: transport.RestoreAsync,
            emergency: (_, token) => transport.ReleaseToFirmwareAsync(token),
            encode: state => new CapabilityValue
            {
                Kind = CapabilityValueKind.Curve,
                CurveValue = [
                    new CurvePoint(state.Channel, state.DutyPercent),
                    new CurvePoint(state.FirmwareControlled ? 1 : 0, state.Rpm ?? -1),
                ],
            },
            cancellationToken);

    /// <summary>Runs one low-amplitude pulse and guarantees zero output in rollback and emergency paths.</summary>
    public static Task<MutationTrialOutcome> RunRumbleAsync(
        MutationTrialExecutionContext context,
        IRumbleTrialTransport transport,
        CancellationToken cancellationToken) => RunAsync(
            context,
            MutationTrialFamily.Rumble,
            read: transport.IsZeroAsync,
            planned: _ => false,
            apply: (_, token) => transport.ApplyLowPulseAsync(token),
            observeApplied: (_, token) => transport.ObservePulseAsync(token),
            restore: (_, token) => transport.ZeroOutputAsync(token),
            emergency: (_, token) => transport.ZeroOutputAsync(token),
            encode: value => new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = value },
            cancellationToken);

    /// <summary>Runs one zone only when the exact profile has already been proven volatile.</summary>
    public static Task<MutationTrialOutcome> RunVolatileRgbAsync(
        MutationTrialExecutionContext context,
        IVolatileRgbTrialTransport transport,
        CancellationToken cancellationToken)
    {
        if (!transport.IsProvenVolatile || !context.Metadata.DeviceVolatile)
        {
            throw new InvalidOperationException("RGB trial refused because volatility is not proven twice.");
        }

        return RunAsync(
            context,
            MutationTrialFamily.VolatileRgbZone,
            read: transport.ReadZoneAsync,
            planned: original => original with { PackedRgb = 0x000020, BrightnessPercent = 5 },
            apply: (state, token) => transport.ApplyLowBrightnessAsync(state.Zone, token),
            observeApplied: async (value, token) =>
            {
                TrialRgbState observed = await transport.ReadZoneAsync(token).ConfigureAwait(false);
                return observed.Zone == value.Zone && observed.BrightnessPercent <= 10;
            },
            restore: transport.RestoreZoneAsync,
            emergency: transport.RestoreZoneAsync,
            encode: state => new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = state.PackedRgb },
            cancellationToken);
    }

    /// <summary>Continues one mode change and exact restore by physical location, never container ID.</summary>
    public static Task<MutationTrialOutcome> RunControllerModeAsync(
        MutationTrialExecutionContext context,
        IControllerModeTrialTransport transport,
        CancellationToken cancellationToken) => RunAsync(
            context,
            MutationTrialFamily.ControllerMode,
            read: transport.ReadStateAsync,
            planned: original => original with { Mode = $"alternate-from-{original.Mode}", ProductId = "pending" },
            apply: (_, token) => transport.SwitchToAlternateAsync(token),
            observeApplied: async (_, token) =>
            {
                TrialControllerModeState original = await transport.ReadStateAsync(token).ConfigureAwait(false);
                TrialControllerModeState alternate = await transport.WaitForAlternateAtLocationAsync(
                    original.PhysicalLocation,
                    token).ConfigureAwait(false);
                return string.Equals(alternate.PhysicalLocation, original.PhysicalLocation, StringComparison.Ordinal)
                    && !string.Equals(alternate.ProductId, original.ProductId, StringComparison.OrdinalIgnoreCase);
            },
            restore: (original, token) => transport.RestoreModeAsync(original.Mode, token),
            emergency: (original, token) => transport.RestoreModeAsync(original.Mode, token),
            encode: state => new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = $"{state.Mode}|{state.ProductId}|{state.PhysicalLocation}",
            },
            cancellationToken,
            verifyRestored: async (original, token) =>
            {
                TrialControllerModeState restored = await transport.WaitForOriginalAtLocationAsync(
                    original.PhysicalLocation,
                    original.ProductId,
                    token).ConfigureAwait(false);
                return restored == original;
            });

    private static async Task<MutationTrialOutcome> RunAsync<T>(
        MutationTrialExecutionContext context,
        MutationTrialFamily family,
        Func<CancellationToken, ValueTask<T>> read,
        Func<T, T> planned,
        Func<T, CancellationToken, ValueTask> apply,
        Func<T, CancellationToken, ValueTask<bool>> observeApplied,
        Func<T, CancellationToken, ValueTask> restore,
        Func<T, CancellationToken, ValueTask> emergency,
        Func<T, CapabilityValue> encode,
        CancellationToken cancellationToken,
        Func<T, CancellationToken, ValueTask<bool>>? verifyRestored = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Metadata.Family != family
            || !MutationTrialAuthorizationPolicy.IsCurrent(
                context.Authorization,
                context.Metadata,
                context.Snapshot))
        {
            throw new InvalidOperationException("Trial family or state-pinned authorization is no longer current.");
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(context.Metadata.TimeoutMilliseconds);
        T original = await read(deadline.Token).ConfigureAwait(false);
        T target = planned(original);
        long sequence = 0;
        int writes = 0;
        bool applied = false;
        bool observed = false;
        ProbeCleanup cleanup = ProbeCleanup.RestoreUnverified;

        await Append(JournalEntryStatus.Planned, original, target, false, default!).ConfigureAwait(false);
        try
        {
            await Append(JournalEntryStatus.Applying, original, target, false, default!).ConfigureAwait(false);
            await CountedWrite(token => apply(target, token)).ConfigureAwait(false);
            applied = true;
            await Append(JournalEntryStatus.AppliedUnverified, original, target, false, default!).ConfigureAwait(false);
            observed = await observeApplied(target, deadline.Token).ConfigureAwait(false);
            if (observed)
            {
                await Append(JournalEntryStatus.AppliedVerified, original, target, true, target).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await CountedWrite(token => restore(original, token)).ConfigureAwait(false);
                await Append(JournalEntryStatus.RestoredUnverified, original, target, observed, target).ConfigureAwait(false);
                bool restored = verifyRestored is null
                    ? EqualityComparer<T>.Default.Equals(await read(deadline.Token).ConfigureAwait(false), original)
                    : await verifyRestored(original, deadline.Token).ConfigureAwait(false);
                cleanup = restored ? ProbeCleanup.RestoredVerified : ProbeCleanup.RestoreUnverified;
                await Append(
                    restored ? JournalEntryStatus.RestoredVerified : JournalEntryStatus.RestoredUnverified,
                    original,
                    target,
                    observed,
                    target).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                try
                {
                    await CountedWrite(token => emergency(original, token)).ConfigureAwait(false);
                }
                catch (Exception emergencyFailure) when (emergencyFailure is not OutOfMemoryException)
                {
                    _ = emergencyFailure;
                }

                cleanup = ProbeCleanup.RestoreFailed;
                await Append(JournalEntryStatus.RestoreFailed, original, target, observed, target)
                    .ConfigureAwait(false);
            }
        }

        ProbeResult result = new()
        {
            Execution = ProbeExecution.Completed,
            Observation = observed ? ProbeObservation.Match : ProbeObservation.Mismatch,
            Mutation = applied
                ? observed ? ProbeMutation.AppliedVerified : ProbeMutation.AppliedUnverified
                : ProbeMutation.None,
            Cleanup = cleanup,
        };
        return new MutationTrialOutcome
        {
            Result = result,
            QuarantinedResourceId = result.Verdict is CompatibilityVerdict.Quarantined
                ? context.Metadata.ResourceId
                : null,
            JournalStates = [.. context.Journal.Read().Entries.Select(entry => entry.Status)],
        };

        async Task CountedWrite(Func<CancellationToken, ValueTask> operation)
        {
            if (++writes > context.Metadata.MaximumWrites)
            {
                throw new InvalidOperationException("Reviewed maximum hardware-write count was exceeded.");
            }

            await operation(deadline.Token).ConfigureAwait(false);
        }

        Task Append(
            JournalEntryStatus status,
            T originalValue,
            T plannedValue,
            bool includeApplied,
            T appliedValue) =>
            context.Journal.AppendAsync(
                new RecoveryJournalEntry
                {
                    Sequence = ++sequence,
                    PackageId = "wsgm.device-lab.reviewed-trial",
                    DeviceId = context.Metadata.BoardId,
                    HostGeneration = context.Snapshot.Preflight.HostGeneration ?? 0,
                    DeviceGeneration = context.Snapshot.Preflight.DeviceGeneration ?? 0,
                    ResourceId = context.Metadata.ResourceId,
                    CapabilityId = context.Metadata.Family.ToString(),
                    FirmwareIdentity = context.Snapshot.FirmwareIdentity,
                    OriginalValue = encode(originalValue),
                    PlannedValue = encode(plannedValue),
                    AppliedValue = includeApplied ? encode(appliedValue) : null,
                    Status = status,
                    OpenedAt = context.Snapshot.Now,
                    ClosedAt = status is JournalEntryStatus.RestoredVerified or JournalEntryStatus.RestoreFailed
                        ? DateTimeOffset.UtcNow
                        : null,
                },
                CancellationToken.None);
    }
}
