using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Read-only widget observations and explicit action routing, independent of package lifecycle.</summary>
internal interface ICommonPluginOverlaySource
{
    /// <summary>Reads current instance presentation without starting packages or touching hardware.</summary>
    /// <returns>A new instance array; referenced declarations remain host-owned immutable snapshots.</returns>
    PluginOverlayInstance[] Snapshot();

    /// <summary>Reads accepted effective state for one instance's current generation.</summary>
    /// <param name="identity">Exact package and instance key.</param>
    /// <returns>A new observation array; empty when the instance is absent or has published no state.</returns>
    PluginStatePublication[] State(PluginInstanceIdentity identity);

    /// <summary>Routes an explicit user action to the current owner.</summary>
    /// <param name="identity">Exact package and instance key.</param>
    /// <param name="generation">Generation shown by the snapshot; stale generations are refused.</param>
    /// <param name="action">Admitted named action ID.</param>
    /// <param name="arguments">User-entered primitive arguments; host validation supplies declared defaults.</param>
    /// <param name="cancellationToken">Cancels waiting without authorizing a retry of uncertain external work.</param>
    /// <returns>The operation-correlated result; dispatch does not imply external-state verification.</returns>
    Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken);
}
