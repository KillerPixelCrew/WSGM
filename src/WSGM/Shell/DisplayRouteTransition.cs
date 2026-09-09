using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Plugin.Sdk;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed record DisplayRouteAction(PluginInstanceIdentity Identity, string ActionId,
    IReadOnlyDictionary<string, PluginValue> Arguments);

internal sealed record DisplayRoutePlan(DisplayRouteAction? Action, DisplayTargetIdentity? Target,
    DisplayProfile? Profile, TimeSpan Timeout)
{
    internal static DisplayRoutePlan FromBinding(DisplayRouteBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.TimeoutSeconds is < 1 or > 120 || binding.Arguments is null || binding.Arguments.Count > 64)
        { throw new ArgumentException("Invalid route timeout or arguments.", nameof(binding)); }
        DisplayRouteAction? action = null;
        if (binding.Plugin is not null || binding.ActionId is not null)
        {
            if (binding.Plugin is not { } identity || string.IsNullOrWhiteSpace(identity.PluginId)
                || string.IsNullOrWhiteSpace(identity.InstanceId) || string.IsNullOrWhiteSpace(binding.ActionId))
            { throw new ArgumentException("Route actions require plugin, instance and action identities.", nameof(binding)); }
            action = new(identity, binding.ActionId, new Dictionary<string, PluginValue>(binding.Arguments));
        }
        return new(action, binding.Target, binding.Profile, TimeSpan.FromSeconds(binding.TimeoutSeconds));
    }
}

internal sealed record DisplayRouteResult(bool Completed, string Stage, string Detail, bool ProfileApplied = false);

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
                return result.Outcome is PluginActionOutcome.AppliedVerified or PluginActionOutcome.Dispatched ? null
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
            return new(true, "complete", "Display route transition completed.", plan.Profile is not null);
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
