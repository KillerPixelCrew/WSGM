using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>The GPU destination projects the common host, without a device lifecycle or vendor-name guessing.</summary>
internal sealed class GpuPluginOverlaySource(ICommonPluginOverlaySource source) : ICommonPluginOverlaySource
{
    public PluginOverlayInstance[] Snapshot() => source.Snapshot()
        .Where(instance => instance.Category == PluginCategories.Gpu
                           && (instance.Controls?.Contributions.Count > 0 || instance.Error is not null || !instance.CanInvoke))
        .ToArray();

    public PluginStatePublication[] State(PluginInstanceIdentity identity) => source.State(identity);

    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation, string action,
        IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken) =>
        source.InvokeAsync(identity, generation, action, arguments, cancellationToken);
}
