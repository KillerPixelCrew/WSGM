using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>
///     Invokes steps against the resident plugin host, resolving each step's generation at the
///     moment it runs so a plugin that restarted between two steps is not addressed with a stale
///     one.
/// </summary>
/// <param name="host">Borrowed resident host used to resolve current instance generations.</param>
internal sealed class PluginHostActionInvoker(PluginHost host) : IPluginActionInvoker
{
    /// <inheritdoc />
    public Task<PluginActionResult> InvokeAsync(
        PluginActionStep step, Deadline deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (step.Plugin is not { } identity || string.IsNullOrWhiteSpace(step.ActionId))
        {
            return Task.FromResult(new PluginActionResult(Guid.NewGuid(), PluginActionOutcome.Rejected,
                "The step does not name a plugin action."));
        }

        var instance = host.Snapshot().FirstOrDefault(value => value.Instance == identity);
        if (instance is null)
        {
            return Task.FromResult(new PluginActionResult(Guid.NewGuid(), PluginActionOutcome.Rejected,
                $"{identity.PluginId} / {identity.InstanceId} is not running."));
        }

        return host.InvokeActionAsync(identity, instance.Generation, step.ActionId, step.Arguments,
            PluginActionOrigin.SessionAutomation, deadline, cancellationToken);
    }
}
