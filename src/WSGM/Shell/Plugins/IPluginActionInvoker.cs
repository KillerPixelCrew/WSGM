using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Resolves a configured step against the live plugin host.</summary>
internal interface IPluginActionInvoker
{
    /// <summary>Invokes one step at its own deadline.</summary>
    /// <param name="step">The configured step.</param>
    /// <param name="deadline">When this step alone must be finished.</param>
    /// <param name="cancellationToken">Cancels the step.</param>
    /// <returns>The plugin's result.</returns>
    Task<PluginActionResult> InvokeAsync(
        PluginActionStep step, Deadline deadline, CancellationToken cancellationToken);
}
