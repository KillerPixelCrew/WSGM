using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>What one publisher's reconciliation pass needs from its owner.</summary>
/// <param name="Label">The publisher's name in the log, such as "Device".</param>
/// <param name="Snapshot">Reads the router's current views.</param>
/// <param name="ExecuteAsync">
///     Writes one admitted value and returns the result, or null when the owner skipped it on its own
///     terms, such as a lighting zone that used up its attempts.
/// </param>
internal sealed record CapabilityReconcilePass(
    string Label,
    Func<IReadOnlyList<DeviceCapabilityView>> Snapshot,
    Func<DeviceCapabilityView, CapabilityValue, CancellationToken, Task<CapabilityCommandResult?>> ExecuteAsync)
{
    /// <summary>Reads one candidate fresh without rebuilding every view.</summary>
    public Func<DeviceCapabilityKey, DeviceCapabilityView?>? ReadCurrent { get; init; }

    /// <summary>Orders the writes; lower values go first.</summary>
    public Func<DeviceCapabilityView, int> Priority { get; init; } = static _ => 0;

    /// <summary>Which capabilities this pass considers at all.</summary>
    public Func<DeviceCapabilityView, bool> Include { get; init; } = static _ => true;

    /// <summary>Checked before each capability; true abandons the rest of the pass.</summary>
    public Func<bool> Abandon { get; init; } = static () => false;

    /// <summary>
    ///     Whether the publisher has reported the capability at all. One that has not is waiting for its first
    ///     state rather than unavailable, so it is skipped without a warning.
    /// </summary>
    public Func<DeviceCapabilityView, bool> HasState { get; init; } = static _ => true;
}

/// <summary>What one reconciliation pass did.</summary>
/// <param name="Applied">Commands reporting application, including accepted writes without independent readback.</param>
/// <param name="Unchanged">Candidates already at their desired value.</param>
/// <param name="Refused">Dispatched commands returning an outcome other than application.</param>
/// <param name="Skipped">Candidates deferred by missing state, availability, value bounds, prior uncertainty, or owner policy.</param>
internal readonly record struct CapabilityReconcileCounts(int Applied, int Unchanged, int Refused, int Skipped);

/// <summary>
///     Writes each capability's desired value when it differs from what the publisher reports, for the
///     device package and every graphics package alike.
/// </summary>
/// <remarks>
///     Per capability and independent: one refusal must not stop the rest, because a profile that
///     applied its fan curve but not its power limit is still better than one that applied nothing. A
///     value the publisher already reports is skipped, so reselecting the active profile is free. Which
///     value is desired, and whether it may be written now, is <see cref="DeviceDesiredWriteAdmission" />'s
///     decision, including each descriptor's profile scope. The caller serializes passes.
/// </remarks>
internal static class CapabilityDesiredReconciler
{
    /// <summary>Runs one pass.</summary>
    /// <param name="pass">The publisher and its hooks.</param>
    /// <param name="reason">Why the pass runs, for the log.</param>
    /// <param name="cancellationToken">Cancels the pass between writes.</param>
    /// <returns>The counts, or null when the pass was abandoned.</returns>
    internal static async Task<CapabilityReconcileCounts?> RunAsync(
        CapabilityReconcilePass pass,
        string reason,
        CancellationToken cancellationToken)
    {
        var applied = 0;
        var unchanged = 0;
        var refused = 0;
        var skipped = 0;
        foreach (var candidate in pass.Snapshot()
                     .OrderBy(pass.Priority)
                     .ThenBy(view => view.Descriptor.CapabilityId, StringComparer.Ordinal)
                     .ThenBy(view => view.Descriptor.InstanceId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pass.Abandon())
            {
                return null;
            }

            // A preceding command can take seconds. Resolve the current layer again instead
            // of replaying the remainder of an obsolete application/profile snapshot.
            var view = pass.ReadCurrent is { } read
                ? read(new DeviceCapabilityKey(candidate.Descriptor.CapabilityId, candidate.Descriptor.InstanceId))
                : pass.Snapshot().FirstOrDefault(current =>
                    string.Equals(current.Descriptor.CapabilityId, candidate.Descriptor.CapabilityId,
                        StringComparison.Ordinal)
                    && string.Equals(current.Descriptor.InstanceId, candidate.Descriptor.InstanceId,
                        StringComparison.Ordinal));
            if (view is null || !pass.Include(view))
            {
                continue;
            }

            if (!pass.HasState(view))
            {
                skipped++;
                continue;
            }

            var admission = DeviceDesiredWriteAdmission.TryAdmit(view);
            if (admission.SkipReason is DeviceDesiredWriteSkipReason.AlreadyApplied)
            {
                unchanged++;
                continue;
            }

            if (!admission.Admitted)
            {
                if (admission.SkipReason is DeviceDesiredWriteSkipReason.Unavailable
                    or DeviceDesiredWriteSkipReason.DesiredValueOutOfRange)
                {
                    skipped++;
                    Log.Warn(
                        $"Desired value not applied for {Name(view)} ({pass.Label}, {reason}): available="
                        + $"{view.Projection.State.Available}, outOfRange="
                        + $"{view.Projection.DesiredValueOutOfRange}.");
                }
                else if (admission.SkipReason is not (DeviceDesiredWriteSkipReason.Unsupported
                         or DeviceDesiredWriteSkipReason.MissingDesiredValue
                         or DeviceDesiredWriteSkipReason.MissingDesiredSource))
                {
                    skipped++;
                }

                continue;
            }

            var result = await pass.ExecuteAsync(view, admission.DesiredValue!, cancellationToken)
                .ConfigureAwait(false);
            if (result is null)
            {
                skipped++;
                continue;
            }

            if (result.Outcome.IsApplied())
            {
                applied++;
                continue;
            }

            refused++;
            Log.Warn(
                $"Desired value refused for {Name(view)} ({pass.Label}, {reason}): outcome={result.Outcome}, "
                + $"{result.Reason?.Detail ?? "no detail"}.");
        }

        Log.Info(
            $"Desired-value reconciliation ({pass.Label}, {reason}): applied={applied}, unchanged={unchanged}, "
            + $"refused={refused}, skipped={skipped}.");
        return new CapabilityReconcileCounts(applied, unchanged, refused, skipped);
    }

    /// <summary>A capability's name in the log.</summary>
    /// <param name="view">Capability whose semantic and optional instance identifiers are formatted.</param>
    /// <returns>The capability identifier alone, or identifiers separated by a slash.</returns>
    internal static string Name(DeviceCapabilityView view)
    {
        return view.Descriptor.InstanceId is { Length: > 0 } instance
            ? $"{view.Descriptor.CapabilityId}/{instance}"
            : view.Descriptor.CapabilityId;
    }
}
