using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>Applies shared WSGM settings through the session's configuration and plugin owners.</summary>
internal interface IWsgmSteamSettingsBackend
{
    /// <summary>Changes one setting.</summary>
    /// <param name="key">The row's key, as published.</param>
    /// <param name="value">The new value, in the row's own shape.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>Whether it was saved, and why not when it was not.</returns>
    Task<SteamUiCommandResult> SetAsync(string key, JsonElement value, CancellationToken cancellationToken);
}
