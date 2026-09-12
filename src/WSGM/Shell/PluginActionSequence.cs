using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>What one configured step did.</summary>
/// <param name="Step">The configured step.</param>
/// <param name="Outcome">The plugin's own outcome.</param>
/// <param name="Detail">A bounded explanation suitable for the splash or the log.</param>
internal sealed record PluginActionStepResult(PluginActionStep Step, PluginActionOutcome Outcome, string Detail)
{
    /// <summary>Whether the step got far enough for the next one to run.</summary>
    internal bool Succeeded => Outcome is PluginActionOutcome.AppliedVerified or PluginActionOutcome.Dispatched;

    /// <summary>Whether the step may have changed something despite not succeeding. Compensation
    /// has to run for these too, because "unknown" is not "nothing happened".</summary>
    internal bool MayHaveActed => Outcome is not PluginActionOutcome.Rejected;
}

/// <summary>Resolves a configured step against the live plugin host.</summary>
internal interface IPluginActionInvoker
{
    /// <summary>Invokes one step at its own deadline.</summary>
    /// <param name="step">The configured step.</param>
    /// <param name="deadline">When this step alone must be finished.</param>
    /// <param name="cancellationToken">Cancels the step.</param>
    /// <returns>The plugin's result.</returns>
    Task<PluginActionResult> InvokeAsync(
        PluginActionStep step, DateTimeOffset deadline, CancellationToken cancellationToken);
}

/// <summary>Runs a configured list of plugin actions in order.
///
/// Two behaviours, because the lists mean different things. Entry stops at the first step that did
/// not succeed: sending the rest after the TV failed to come on only makes the failure harder to
/// read. Leave, desktop startup and desktop wake run every step and report what failed, because
/// each one is independently worth attempting and there is nothing to abort. Nothing is ever
/// retried in either mode.</summary>
internal sealed class PluginActionSequence(IPluginActionInvoker invoker)
{
    /// <summary>Runs steps in order, stopping at the first that did not succeed.</summary>
    /// <param name="steps">Configured steps.</param>
    /// <param name="cancellationToken">Cancels the sequence between and during steps.</param>
    /// <returns>One result per step that ran.</returns>
    internal Task<IReadOnlyList<PluginActionStepResult>> RunUntilFailureAsync(
        IReadOnlyList<PluginActionStep> steps, CancellationToken cancellationToken) =>
        RunAsync(steps, stopOnFailure: true, cancellationToken);

    /// <summary>Runs every step, reporting each.</summary>
    /// <param name="steps">Configured steps.</param>
    /// <param name="cancellationToken">Cancels the sequence between and during steps.</param>
    /// <returns>One result per step.</returns>
    internal Task<IReadOnlyList<PluginActionStepResult>> RunAllAsync(
        IReadOnlyList<PluginActionStep> steps, CancellationToken cancellationToken) =>
        RunAsync(steps, stopOnFailure: false, cancellationToken);

    /// <summary>Whether any step in a partial run may have changed external state.</summary>
    /// <param name="results">Results from an earlier run.</param>
    /// <returns>True when compensation is owed.</returns>
    internal static bool NeedsCompensation(IEnumerable<PluginActionStepResult> results) =>
        results.Any(result => result.MayHaveActed);

    private async Task<IReadOnlyList<PluginActionStepResult>> RunAsync(
        IReadOnlyList<PluginActionStep> steps, bool stopOnFailure, CancellationToken cancellationToken)
    {
        List<PluginActionStepResult> results = [];
        foreach (PluginActionStep step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PluginActionStepResult result = await RunStepAsync(step, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (stopOnFailure && !result.Succeeded) { break; }
        }
        return results;
    }

    private async Task<PluginActionStepResult> RunStepAsync(
        PluginActionStep step, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(step.TimeoutSeconds, 1, 120));
        deadline.CancelAfter(timeout);
        try
        {
            PluginActionResult result = await invoker
                .InvokeAsync(step, DateTimeOffset.UtcNow + timeout, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            return new(step, result.Outcome, result.Detail ?? result.Outcome.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled mid-step. The plugin may already have emitted, so this is
            // uncertain rather than rejected, and the caller owes it compensation.
            return new(step, PluginActionOutcome.Unconfirmed,
                "Cancelled while the action was running; it may already have taken effect.");
        }
        catch (OperationCanceledException)
        {
            return new(step, PluginActionOutcome.Unconfirmed,
                $"No answer within {timeout.TotalSeconds:0} seconds; the action may still take effect.");
        }
        catch (Exception ex)
        {
            return new(step, PluginActionOutcome.Unconfirmed, "Unconfirmed: " + ex.Message);
        }
    }
}

/// <summary>Invokes steps against the resident plugin host, resolving each step's generation at the
/// moment it runs so a plugin that restarted between two steps is not addressed with a stale
/// one.</summary>
internal sealed class PluginHostActionInvoker(PluginHost host) : IPluginActionInvoker
{
    /// <inheritdoc />
    public Task<PluginActionResult> InvokeAsync(
        PluginActionStep step, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (step.Plugin is not { } identity || string.IsNullOrWhiteSpace(step.ActionId))
        {
            return Task.FromResult(new PluginActionResult(Guid.NewGuid(), PluginActionOutcome.Rejected,
                "The step does not name a plugin action."));
        }
        PluginHealthPublication? instance = host.Snapshot().FirstOrDefault(value => value.Instance == identity);
        if (instance is null)
        {
            return Task.FromResult(new PluginActionResult(Guid.NewGuid(), PluginActionOutcome.Rejected,
                $"{identity.PluginId} / {identity.InstanceId} is not running."));
        }
        return host.InvokeActionAsync(identity, instance.Generation, step.ActionId, step.Arguments,
            PluginActionOrigin.SessionAutomation, deadline, cancellationToken);
    }
}
