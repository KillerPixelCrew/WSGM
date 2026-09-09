using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Connects route intent to the resident plugin host and reusable Windows display APIs.</summary>
internal sealed class DisplayRouteBackend(PluginHost host) : IDisplayRouteBackend
{
    public Task<PluginActionResult> InvokeAsync(DisplayRouteAction action, DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var instance = host.Snapshot().FirstOrDefault(value => value.Instance == action.Identity);
        if (instance is null)
        {
            return Task.FromResult(new PluginActionResult(Guid.NewGuid(), PluginActionOutcome.Rejected,
                "The configured route plugin instance is unavailable."));
        }
        return host.InvokeActionAsync(action.Identity, instance.Generation, action.ActionId, action.Arguments,
            PluginActionOrigin.SessionAutomation, deadline, cancellationToken);
    }

    public Task<DisplayWaitOutcome> WaitAsync(DisplayTargetIdentity target, TimeSpan timeout,
        CancellationToken cancellationToken) => Task.Run(async () =>
        {
            return await DisplayTopology.WaitForAvailableAsync(target, timeout, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<DisplayProfileResult> ApplyAsync(DisplayProfile profile, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DisplayTopology.ApplyProfile(profile);
        }, cancellationToken);
}
