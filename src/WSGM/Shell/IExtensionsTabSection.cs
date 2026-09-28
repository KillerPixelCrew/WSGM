using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>One of WSGM's own sections on the Quick Access Extensions tab: the themes, the boot movie.</summary>
/// <remarks>
///     The tab's backend lists the sections and routes by id: an action whose id starts with the
///     section's id and a dot is the section's, and a setting on the section's item is too. A new
///     section is one more entry in that list, not another branch in the backend.
/// </remarks>
internal interface IExtensionsTabSection
{
    /// <summary>The item id the section is published under; its actions are this id, a dot and a name.</summary>
    string SectionId { get; }

    /// <summary>The section as the tab shows it now.</summary>
    /// <returns>The item.</returns>
    SteamExtensionsTabItem ReadExtensionsItem();

    /// <summary>Answers one of the section's actions.</summary>
    /// <param name="id">The action id.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result, a route for an action that opens a page.</returns>
    Task<SteamUiCommandResult> ActivateExtensionAsync(string id, CancellationToken cancellationToken);

    /// <summary>Applies one of the section's settings.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="value">Its new value.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result.</returns>
    Task<SteamUiCommandResult> ConfigureExtensionAsync(string key, JsonElement value,
        CancellationToken cancellationToken);
}
