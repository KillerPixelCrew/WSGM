// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Asus.RogAlly;

internal enum AllyServiceState
{
    Idle,
    Acquiring,
    Owned,
    Passive,
    Degraded,
    Releasing,
    ReleasedUnverified,
    Faulted
}

/// <summary>Live facts one device cycle is running against.</summary>
internal readonly record struct AllyCycleContext(
    long CycleGeneration,
    DateTimeOffset Deadline,
    AllyIdentityState Identity);

internal sealed record AllyServiceResult(AllyServiceState State, CapabilityReason? Reason = null);

/// <summary>A resource the plugin acquires for a cycle and releases when the cycle ends.</summary>
/// <remarks>The same state machine as the Claw reference plugin's <c>ClawCycleService</c>.</remarks>
internal abstract class AllyService(string serviceId)
{
    public string ServiceId { get; } = serviceId;

    public AllyServiceState State { get; protected set; } = AllyServiceState.Idle;

    public CapabilityReason? Reason { get; private set; }

    /// <summary>Set when an outstanding recovery entry makes acquiring this service unsafe.</summary>
    public CapabilityReason? ReconciliationBlockReason { get; set; }

    /// <summary>Whether the service stops for suspend and is reacquired on resume.</summary>
    public virtual bool Suspendable => false;

    public abstract ValueTask<AllyServiceResult> AcquireAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken);

    public abstract ValueTask<AllyServiceResult> ReleaseAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken);

    public virtual ValueTask<AllyServiceResult> SuspendAsync(
        AllyCycleContext context,
        CancellationToken cancellationToken)
    {
        return ReleaseAsync(context, cancellationToken);
    }

    public void ApplyResult(AllyServiceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        State = result.State;
        Reason = result.Reason;
    }

    /// <summary>Marks the service faulted until it is next acquired.</summary>
    /// <remarks>
    ///     Unlike <see cref="ReconciliationBlockReason" />, a fault does not outlive the cycle: resume and
    ///     controller re-enablement acquire the service again.
    /// </remarks>
    public void Fault(CapabilityReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        _ = Set(AllyServiceState.Faulted, reason);
    }

    protected AllyServiceResult Set(AllyServiceState state, CapabilityReason? reason = null)
    {
        State = state;
        Reason = reason;
        return new AllyServiceResult(state, reason);
    }

    protected static CapabilityReason Missing(string detail)
    {
        return new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing, detail);
    }
}

/// <summary>Command results shared by every capability.</summary>
internal static class AllyResults
{
    public static CapabilityCommandResult Verified(CapabilityCommand command, CapabilityValue readback)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedVerified,
            ReadbackValue = readback,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>A write the device accepted but that nothing can read back.</summary>
    public static CapabilityCommandResult Unverified(CapabilityCommand command, string detail)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.AppliedUnverified,
            Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    public static CapabilityCommandResult Rejected(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail,
        bool retryable = false)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = new CapabilityReason(code, detail, retryable),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    public static CapabilityCommandResult Rejected(CapabilityCommand command, CapabilityReason reason)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = reason,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    public static CapabilityCommandResult Indeterminate(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail,
        RollbackResult rollback)
    {
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(code, detail),
            Rollback = rollback,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>The one minimum budget required before any hardware write.</summary>
internal static class AllyWriteBudget
{
    private static readonly TimeSpan Minimum = TimeSpan.FromSeconds(2);

    public static bool IsAvailable(DateTimeOffset deadline)
    {
        return deadline - DateTimeOffset.UtcNow >= Minimum;
    }

    /// <summary>Throws <see cref="AllyBudgetException" /> when the deadline leaves too little time.</summary>
    public static void Require(DateTimeOffset deadline, string operation)
    {
        if (!IsAvailable(deadline))
        {
            throw new AllyBudgetException($"Insufficient budget for {operation}.");
        }
    }
}

/// <summary>A write was refused for lack of time before anything reached the hardware.</summary>
internal sealed class AllyBudgetException(string message) : Exception(message);

internal static class AllyDiagnosticText
{
    public static string FromException(string context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = $"{context} ({exception.GetType().Name}): {exception.Message}";
        var length = Math.Min(message.Length, PluginTrace.MaxMessageLength);
        var bounded = new char[length];
        for (var index = 0; index < bounded.Length; index++)
        {
            var character = message[index];
            bounded[index] = PlainText.IsUnsafe(character) ? ' ' : character;
        }

        return new string(bounded);
    }
}
