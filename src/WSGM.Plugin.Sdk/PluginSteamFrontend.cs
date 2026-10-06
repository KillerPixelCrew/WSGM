using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Plugin.Sdk;

/// <summary>Optional JSON backend for a package's unrestricted Steam frontend modules.</summary>
/// <remarks>Frontend-only packages do not implement this interface. This is not a sandbox.</remarks>
public interface IPluginSteamFrontend
{
    /// <summary>Handles one request from an admitted frontend in the current plugin generation.</summary>
    /// <param name="moduleId">The manifest module that sent the request.</param>
    /// <param name="method">Package-defined method name.</param>
    /// <param name="payload">Package-defined JSON input.</param>
    /// <param name="cancellationToken">Canceled when the request or plugin stops.</param>
    /// <returns>Package-defined JSON response.</returns>
    Task<JsonElement?> InvokeFrontendAsync(string moduleId, string method, JsonElement payload,
        CancellationToken cancellationToken);

    /// <summary>Reads optional JSON state published to the named frontend.</summary>
    /// <param name="moduleId">The manifest module receiving the publication.</param>
    /// <returns>Detached state, or null when nothing should be published.</returns>
    JsonElement? ReadFrontendState(string moduleId)
    {
        return null;
    }

    /// <summary>Requests a fresh state publication after backend state changes.</summary>
    event Action? FrontendChanged
    {
        add { }
        remove { }
    }
}
