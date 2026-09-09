using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

internal sealed record DisplayRouteAction(PluginInstanceIdentity Identity, string ActionId,
    IReadOnlyDictionary<string, PluginValue> Arguments);

internal sealed record DisplayRoutePlan(DisplayRouteAction? Action, DisplayTargetIdentity? Target,
    DisplayProfile? Profile, TimeSpan Timeout);

internal sealed record DisplayRouteResult(bool Completed, string Stage, string Detail);

/// <summary>Adapters own plugin admission and Windows calls; the transition owns ordering only.</summary>
internal interface IDisplayRouteBackend
{
    Task<PluginActionResult> InvokeAsync(DisplayRouteAction action, DateTimeOffset deadline, CancellationToken cancellationToken);
    Task<DisplayWaitOutcome> WaitAsync(DisplayTargetIdentity target, TimeSpan timeout, CancellationToken cancellationToken);
    Task<DisplayProfileResult> ApplyAsync(DisplayProfile profile, CancellationToken cancellationToken);
}

/// <summary>Runs a single route transition without retries or device-specific routing knowledge.</summary>
internal sealed class DisplayRouteTransition(IDisplayRouteBackend backend)
{
    internal async Task<DisplayRouteResult> RunAsync(DisplayRoutePlan plan, bool enteringGameMode,
        CancellationToken cancellationToken)
    {
        if (plan.Timeout < TimeSpan.FromSeconds(1) || plan.Timeout > TimeSpan.FromMinutes(2))
        { return new(false, "configuration", "Route timeout must be between 1 and 120 seconds."); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(plan.Timeout);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + plan.Timeout;
        string stage = "starting";
        try
        {
            async Task<DisplayRouteResult?> ActionAsync()
            {
                if (plan.Action is null) { return null; }
                stage = "plugin action";
                timeout.Token.ThrowIfCancellationRequested();
                var result = await backend.InvokeAsync(plan.Action, deadline, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                return result.Outcome == PluginActionOutcome.AppliedVerified ? null
                    : new(false, stage, result.Detail ?? result.Outcome.ToString());
            }
            async Task<DisplayRouteResult?> ProfileAsync()
            {
                if (plan.Profile is null) { return null; }
                stage = "display profile";
                timeout.Token.ThrowIfCancellationRequested();
                var result = await backend.ApplyAsync(plan.Profile, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                return result.Applied ? null : new(false, stage, result.Detail);
            }
            if (enteringGameMode)
            {
                if (await ActionAsync().ConfigureAwait(false) is { } actionFailure) { return actionFailure; }
                if (plan.Target is { } target)
                {
                    stage = "target display";
                    timeout.Token.ThrowIfCancellationRequested();
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero) { return new(false, stage, "Route deadline elapsed."); }
                    var outcome = await backend.WaitAsync(target, remaining, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                    if (outcome != DisplayWaitOutcome.Present) { return new(false, stage, "Target display did not appear."); }
                }
                if (await ProfileAsync().ConfigureAwait(false) is { } profileFailure) { return profileFailure; }
            }
            else
            {
                if (await ProfileAsync().ConfigureAwait(false) is { } profileFailure) { return profileFailure; }
                if (await ActionAsync().ConfigureAwait(false) is { } actionFailure) { return actionFailure; }
            }
            return new(true, "complete", "Display route transition completed.");
        }
        catch (OperationCanceledException)
        {
            return new(false, stage, cancellationToken.IsCancellationRequested
                ? "Route transition cancelled; an in-flight write may be unconfirmed."
                : "Route deadline elapsed; an in-flight write may be unconfirmed.");
        }
        catch (Exception ex) { return new(false, stage, "Unconfirmed: " + ex.Message); }
    }
}
