using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>
///     Runs a configured list of plugin actions in order.
///     Two behaviours, because the lists mean different things. Entry stops at the first step that did
///     not succeed: sending the rest after the TV failed to come on only makes the failure harder to
///     read. Leave, desktop startup and desktop wake run every step and report what failed, because
///     each one is independently worth attempting and there is nothing to abort. Nothing is ever
///     retried in either mode.
/// </summary>
/// <param name="invoker">Borrowed dispatcher resolving each action against the current plugin generation.</param>
/// <param name="log">Optional transition logger; must not throw or record secret arguments.</param>
internal sealed class PluginActionSequence(IPluginActionInvoker invoker, Action<string>? log = null)
{
    /// <summary>Runs steps in order, stopping at the first that did not succeed.</summary>
    /// <param name="steps">Configured steps.</param>
    /// <param name="cancellationToken">Cancels the sequence between and during steps.</param>
    /// <returns>One result per step that ran.</returns>
    internal Task<IReadOnlyList<PluginActionStepResult>> RunUntilFailureAsync(
        IReadOnlyList<PluginActionStep> steps, CancellationToken cancellationToken)
    {
        return RunAsync(steps, true, cancellationToken);
    }

    /// <summary>Runs every step, reporting each.</summary>
    /// <param name="steps">Configured steps.</param>
    /// <param name="cancellationToken">Cancels the sequence between and during steps.</param>
    /// <returns>One result per attempted step; caller cancellation leaves later steps unattempted.</returns>
    internal Task<IReadOnlyList<PluginActionStepResult>> RunAllAsync(
        IReadOnlyList<PluginActionStep> steps, CancellationToken cancellationToken)
    {
        return RunAsync(steps, false, cancellationToken);
    }

    /// <summary>Whether any step in a partial run may have changed external state.</summary>
    /// <param name="results">Results from an earlier run.</param>
    /// <returns>True when compensation is owed.</returns>
    internal static bool NeedsCompensation(IEnumerable<PluginActionStepResult> results)
    {
        return results.Any(result => result.MayHaveActed);
    }

    private async Task<IReadOnlyList<PluginActionStepResult>> RunAsync(
        IReadOnlyList<PluginActionStep> steps, bool stopOnFailure, CancellationToken cancellationToken)
    {
        List<PluginActionStepResult> results = [];
        foreach (var step in steps)
        {
            // Keep outcomes already observed. Throwing between steps discards the evidence
            // needed to compensate actions that have already reached the appliance.
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var action = $"{step.Plugin?.PluginId}/{step.Plugin?.InstanceId} {step.ActionId}";
            log?.Invoke($"Session action starting: {action}.");
            var result = await RunStepAsync(step, cancellationToken).ConfigureAwait(false);
            log?.Invoke($"Session action completed: {action}: {result.Outcome}; {result.Detail}");
            results.Add(result);
            if (stopOnFailure && !result.Succeeded)
            {
                break;
            }
        }

        return results;
    }

    private async Task<PluginActionStepResult> RunStepAsync(
        PluginActionStep step, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(step.TimeoutSeconds, PluginActionStep.MinimumTimeoutSeconds,
            PluginActionStep.MaximumTimeoutSeconds));
        deadline.CancelAfter(timeout);
        try
        {
            var result = await invoker
                .InvokeAsync(step, Deadline.After(timeout), deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            return new PluginActionStepResult(step, result.Outcome, result.Detail ?? result.Outcome.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled mid-step. The plugin may already have emitted, so this is
            // uncertain rather than rejected, and the caller owes it compensation.
            return new PluginActionStepResult(step, PluginActionOutcome.Unconfirmed,
                "Cancelled while the action was running; it may already have taken effect.");
        }
        catch (OperationCanceledException)
        {
            return new PluginActionStepResult(step, PluginActionOutcome.Unconfirmed,
                $"No answer within {timeout.TotalSeconds:0} seconds; the action may still take effect.");
        }
        catch (Exception ex)
        {
            return new PluginActionStepResult(step, PluginActionOutcome.Unconfirmed, "Unconfirmed: " + ex.Message);
        }
    }
}
