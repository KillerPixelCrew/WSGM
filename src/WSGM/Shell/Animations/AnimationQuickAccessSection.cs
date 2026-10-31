using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The boot movie's section of Steam's Quick Access Extensions tab, over the shared animation service.</summary>
/// <remarks>The two actions that open the Animations page choose its tab through the Steam page's own browse session.</remarks>
internal sealed class AnimationQuickAccessSection : IExtensionsTabSection
{
    /// <summary>The section's item id.</summary>
    internal const string ExtensionsId = "wsgm.animations";

    /// <summary>The action that opens the Browse tab.</summary>
    internal const string ExtensionsBrowseId = "wsgm.animations.browse";

    /// <summary>The action that opens the Library tab.</summary>
    internal const string ExtensionsManageId = "wsgm.animations.manage";

    /// <summary>The action that picks the boot movie anew.</summary>
    internal const string ExtensionsShuffleId = "wsgm.animations.shuffle";

    private readonly AnimationService _owner;
    private readonly IAnimationBrowseSession _page;

    /// <summary>Creates the section.</summary>
    /// <param name="owner">The shared animation service the settings and commands go to.</param>
    /// <param name="page">The Steam Animations page's browse session, whose tab the opening actions choose.</param>
    internal AnimationQuickAccessSection(AnimationService owner, IAnimationBrowseSession page)
    {
        _owner = owner;
        _page = page;
    }

    /// <inheritdoc />
    public string SectionId => ExtensionsId;

    /// <summary>The Quick Access section: the boot choice, the shuffle, and the way to the page.</summary>
    /// <returns>The item.</returns>
    public SteamExtensionsTabItem ReadExtensionsItem()
    {
        var state = _owner.ReadState();
        var library = state.Library;
        var detail = library.Count == 0 ? "No movies yet" : $"{library.Count} in the library";
        if (state.Settings.RestartNeeded)
        {
            detail += " · Restart Steam to see the change";
        }

        return new SteamExtensionsTabItem(
            ExtensionsId,
            "Boot animation",
            string.Empty,
            state.Busy ? "Working…" : "Ready",
            detail,
            [
                new SteamExtensionsTabAction(ExtensionsBrowseId, "Browse movies…"),
                new SteamExtensionsTabAction(ExtensionsManageId, "Library…"),
                new SteamExtensionsTabAction(ExtensionsShuffleId, "Shuffle")
            ],
            [
                new SteamExtensionsTabSetting("boot", "Boot movie", "text", TextValue: state.Selected,
                    Choices: [string.Empty, .. library.Select(entry => entry.Id)],
                    ChoiceLabels: [AnimationService.StockLabel, .. library.Select(entry => entry.Name)]),
                new SteamExtensionsTabSetting("shuffleOnStart", "Shuffle on start", "boolean",
                    state.Settings.ShuffleOnStart,
                    Description: "Picks the boot movie anew from the library each time WSGM starts.")
            ],
            state.Revision);
    }

    /// <summary>Answers one of the section's actions.</summary>
    /// <param name="id">The action id.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result, carrying the page's route for the two that open it.</returns>
    public async Task<SteamUiCommandResult> ActivateExtensionAsync(string id, CancellationToken cancellationToken)
    {
        switch (id)
        {
            case ExtensionsBrowseId:
                await _page.SetTabAsync("browse", cancellationToken).ConfigureAwait(false);
                return SteamUiCommandResult.Route(SteamAnimationsSurface.Route);
            case ExtensionsManageId:
                await _page.SetTabAsync("library", cancellationToken).ConfigureAwait(false);
                return SteamUiCommandResult.Route(SteamAnimationsSurface.Route);
            case ExtensionsShuffleId:
                return await _owner.ShuffleAsync(cancellationToken).ConfigureAwait(false);
            default:
                return new SteamUiCommandResult(false, "That entry is no longer available.");
        }
    }

    /// <summary>Applies one of the section's settings: the boot movie by library id, or the shuffle.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="value">Its new value.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result.</returns>
    public Task<SteamUiCommandResult> ConfigureExtensionAsync(
        string key, JsonElement value, CancellationToken cancellationToken)
    {
        return (key, value.ValueKind) switch
        {
            ("boot", JsonValueKind.String) => _owner.SelectAsync(value.GetString() ?? string.Empty,
                cancellationToken),
            ("shuffleOnStart", JsonValueKind.True or JsonValueKind.False) =>
                _owner.SetShuffleOnStartAsync(value.GetBoolean(), cancellationToken),
            _ => Task.FromResult(new SteamUiCommandResult(false, "That setting is no longer available."))
        };
    }
}
