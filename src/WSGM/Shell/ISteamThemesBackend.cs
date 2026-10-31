using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>Answers theme commands shared by Steam pages and overlay browse sessions.</summary>
/// <remarks>
///     Accepted store and installation work belongs to the service lifetime; observe Busy, Loading and Error for
///     completion. Display names do not substitute for store or installed-theme identities.
/// </remarks>
internal interface ISteamThemesBackend
{
    /// <summary>Shows one of the page's tabs.</summary>
    /// <param name="tab">browse, installed, profiles or settings.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the active-tab state was updated.</returns>
    Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken);

    /// <summary>Asks the store for the first page matching a filter, order and search.</summary>
    /// <param name="filter">Store filter id; empty selects all themes.</param>
    /// <param name="order">Store sort id; empty selects the default order.</param>
    /// <param name="search">Search text; empty adds no text restriction.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Acceptance of the browse request; results and errors arrive through publications.</returns>
    Task<SteamUiCommandResult> BrowseAsync(string filter, string order, string search,
        CancellationToken cancellationToken);

    /// <summary>Asks the store for the next page of the current listing.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether another page was accepted, or a refusal while loading or after the final result.</returns>
    Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken);

    /// <summary>Opens one store listing's details.</summary>
    /// <param name="id">DeckThemes store theme id.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Acceptance of the detail request; loading completes through published state.</returns>
    Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken);

    /// <summary>Closes the details.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>A successful result after closing the detail selection.</returns>
    Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken);

    /// <summary>Installs a theme from the store, with the dependencies it lacks.</summary>
    /// <param name="id">DeckThemes store theme id.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether installation was accepted; Busy, Notice and Error report its eventual outcome.</returns>
    Task<SteamUiCommandResult> InstallAsync(string id, CancellationToken cancellationToken);

    /// <summary>Installs the store's newer version of an installed theme.</summary>
    /// <param name="name">Installed theme name, distinct from its store id.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether an available update was accepted, or a refusal when none is known.</returns>
    Task<SteamUiCommandResult> UpdateAsync(string name, CancellationToken cancellationToken);

    /// <summary>Installs every newer version the store has.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the known update batch was accepted, or a refusal when every installed theme is current.</returns>
    Task<SteamUiCommandResult> UpdateAllAsync(CancellationToken cancellationToken);

    /// <summary>Turns a theme off and removes its folder.</summary>
    /// <param name="name">Installed theme name.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether removal completed, or the loader/busy refusal.</returns>
    Task<SteamUiCommandResult> DeleteAsync(string name, CancellationToken cancellationToken);

    /// <summary>Turns a theme on or off.</summary>
    /// <param name="name">Installed theme name.</param>
    /// <param name="enabled">Requested active state.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the setting was saved and loader state refreshed, or a refusal.</returns>
    Task<SteamUiCommandResult> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken);

    /// <summary>Chooses a patch's option.</summary>
    /// <param name="theme">Installed theme name.</param>
    /// <param name="patch">Patch name declared by that theme.</param>
    /// <param name="value">Declared option value, including Yes/No for checkbox patches.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the loader accepted and saved the option, or its refusal.</returns>
    Task<SteamUiCommandResult> SetPatchAsync(string theme, string patch, string value,
        CancellationToken cancellationToken);

    /// <summary>Sets a component's colour or image.</summary>
    /// <param name="theme">Installed theme name.</param>
    /// <param name="patch">Patch containing the selected option.</param>
    /// <param name="component">Component name declared in that option.</param>
    /// <param name="value">Color or image value validated by the theme loader.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the component value was accepted and saved, or a refusal.</returns>
    Task<SteamUiCommandResult> SetComponentAsync(
        string theme, string patch, string component, string value, CancellationToken cancellationToken);

    /// <summary>Turns a profile on, or every profile off with an empty name.</summary>
    /// <param name="name">Existing profile name, or empty to disable profiles.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether profile activation was saved, or a refusal.</returns>
    Task<SteamUiCommandResult> SetProfileAsync(string name, CancellationToken cancellationToken);

    /// <summary>Saves the enabled themes and their patch values as a profile.</summary>
    /// <param name="name">Nonempty name for a new profile of the current theme choices.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether the profile was created, or a name/persistence refusal.</returns>
    Task<SteamUiCommandResult> CreateProfileAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads the themes folder again and checks for updates.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether rereading the theme folder and checking store updates was accepted.</returns>
    Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Keeps a theme off the Quick Access section, or shows it there again.</summary>
    /// <param name="name">Installed theme name.</param>
    /// <param name="hidden">Whether to omit it from Quick Access without changing its enabled state.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>Whether visibility was saved, or a refusal.</returns>
    Task<SteamUiCommandResult> SetHiddenAsync(string name, bool hidden, CancellationToken cancellationToken);

    /// <summary>Changes one of the Settings tab's values.</summary>
    /// <param name="key">enabled or translationsBranch.</param>
    /// <param name="value">Boolean for enabled, or branch string for translationsBranch.</param>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>The settings result, or refusal for an unknown key or incompatible value.</returns>
    Task<SteamUiCommandResult> SetSettingAsync(string key, JsonElement value, CancellationToken cancellationToken);

    /// <summary>Clears the notice and the error.</summary>
    /// <param name="cancellationToken">Request cancellation signal; it does not revoke work already accepted by the owner.</param>
    /// <returns>A successful result after clearing both status messages.</returns>
    Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken);
}
