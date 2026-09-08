using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Routes overlay intent to the resident common host without giving views lifecycle ownership.</summary>
internal sealed class CommonPluginOverlaySource(CommonPluginManager manager, PluginHost host)
{
    internal CommonPluginInstanceView[] Snapshot() => manager.Snapshot();
    internal PluginStatePublication[] State(PluginInstanceIdentity identity) => host.StateSnapshot(identity);
    internal Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
        => host.InvokeActionAsync(identity, generation, action, arguments, PluginActionOrigin.User,
            DateTimeOffset.UtcNow.AddSeconds(10), cancellationToken);
}
