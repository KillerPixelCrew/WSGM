using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The themes' section of Steam's Quick Access Extensions tab, over the shared theme service.</summary>
/// <remarks>The two actions that open the Themes page choose its tab through the Steam page's own browse session.</remarks>
internal sealed class ThemeQuickAccessSection : IExtensionsTabSection
{
    /// <summary>The section's item id.</summary>
    internal const string ExtensionsId = "wsgm.themes";

    /// <summary>The action that opens the Browse tab.</summary>
    internal const string ExtensionsBrowseId = "wsgm.themes.browse";

    /// <summary>The action that opens the Installed tab.</summary>
    internal const string ExtensionsManageId = "wsgm.themes.manage";

    /// <summary>The action that installs every update.</summary>
    internal const string ExtensionsUpdateAllId = "wsgm.themes.update-all";

    /// <summary>The action that reads the folder again.</summary>
    internal const string ExtensionsRefreshId = "wsgm.themes.refresh";

    private readonly ThemeService _owner;
    private readonly IThemeBrowseSession _page;

    /// <summary>Creates the section.</summary>
    /// <param name="owner">The shared theme service the settings and commands go to.</param>
    /// <param name="page">The Steam Themes page's browse session, whose tab the opening actions choose.</param>
    internal ThemeQuickAccessSection(ThemeService owner, IThemeBrowseSession page)
    {
        _owner = owner;
        _page = page;
    }

    /// <inheritdoc />
    public string SectionId => ExtensionsId;

    /// <summary>The Quick Access section: the profile, every theme not hidden, and its patches under it.</summary>
    /// <returns>The item.</returns>
    public SteamExtensionsTabItem ReadExtensionsItem()
    {
        var state = _owner.ReadState();
        List<SteamExtensionsTabAction> actions =
        [
            new(ExtensionsBrowseId, "Browse themes…"),
            new(ExtensionsManageId, "Manage…")
        ];
        if (state.Updates > 0)
        {
            actions.Add(new SteamExtensionsTabAction(ExtensionsUpdateAllId, $"Update all ({state.Updates})"));
        }

        actions.Add(new SteamExtensionsTabAction(ExtensionsRefreshId, "Refresh"));

        List<SteamExtensionsTabSetting> settings = [];
        if (state.Presets.Count > 0)
        {
            settings.Add(new SteamExtensionsTabSetting(
                "profile", "Profile", "text",
                TextValue: state.Presets.Any(preset => preset.Name == state.SelectedPreset) ? state.SelectedPreset : "",
                Choices: ["", .. state.Presets.Select(preset => preset.Name)],
                ChoiceLabels: ["None", .. state.Presets.Select(preset => preset.DisplayName)]));
        }

        foreach (var theme in state.Themes.Where(theme => !theme.Hidden))
        {
            var key = ExtensionsKey("theme", theme.Name);
            var description = theme.Status == ThemeStates.Outdated
                ? $"Update available · {theme.Author}"
                : string.IsNullOrEmpty(theme.Author)
                    ? theme.Version
                    : $"{theme.Version} · {theme.Author}";
            settings.Add(new SteamExtensionsTabSetting(key, theme.DisplayName, "boolean", theme.Enabled,
                Description: description, Highlight: theme.Status == ThemeStates.Outdated));
            foreach (var patch in theme.Patches)
            {
                var patchKey = ExtensionsKey("patch", theme.Name, patch.Name);
                switch (patch.Type)
                {
                    case "checkbox":
                        settings.Add(new SteamExtensionsTabSetting(
                            patchKey, patch.Name, "boolean", patch.Value == "Yes", Parent: key));
                        break;
                    case "slider":
                        settings.Add(new SteamExtensionsTabSetting(
                            patchKey, patch.Name, "number",
                            NumberValue: Math.Max(0, patch.Options.ToList().IndexOf(patch.Value)),
                            Choices: patch.Options, Parent: key));
                        break;
                    case "none":
                        break;
                    default:
                        settings.Add(new SteamExtensionsTabSetting(
                            patchKey, patch.Name, "text", TextValue: patch.Value, Choices: patch.Options, Parent: key));
                        break;
                }

                foreach (var component in patch.Components.Where(component => component.On == patch.Value))
                {
                    settings.Add(new SteamExtensionsTabSetting(
                        ExtensionsKey("component", theme.Name, patch.Name, component.Name),
                        component.Name,
                        component.Type == "color-picker" ? "color" : "text",
                        TextValue: component.Value,
                        Parent: key));
                }
            }
        }

        var hiddenCount = state.Themes.Count(theme => theme.Hidden);
        var detail = !state.Settings.Enabled
            ? "Off in Settings"
            : state.Themes.Count == 0
                ? "No themes installed"
                : $"{state.Themes.Count(theme => theme.Enabled)} of {state.Themes.Count} enabled"
                  + (state.Updates > 0 ? $" · {state.Updates} update{(state.Updates == 1 ? "" : "s")}" : string.Empty)
                  + (hiddenCount > 0 ? $" · {hiddenCount} hidden" : string.Empty);
        return new SteamExtensionsTabItem(
            ExtensionsId,
            "Themes",
            string.Empty,
            state.Busy ? "Working…" : "Ready",
            detail,
            actions,
            settings,
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
                return SteamUiCommandResult.Route(SteamThemesSurface.Route);
            case ExtensionsManageId:
                await _page.SetTabAsync("installed", cancellationToken).ConfigureAwait(false);
                return SteamUiCommandResult.Route(SteamThemesSurface.Route);
            case ExtensionsUpdateAllId:
                return await _owner.UpdateAllAsync(cancellationToken).ConfigureAwait(false);
            case ExtensionsRefreshId:
                return await _owner.RefreshAsync(cancellationToken).ConfigureAwait(false);
            default:
                return new SteamUiCommandResult(false, "That entry is no longer available.");
        }
    }

    /// <summary>Answers one of the section's settings.</summary>
    /// <param name="key">The setting key.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result.</returns>
    public Task<SteamUiCommandResult> ConfigureExtensionAsync(
        string key, JsonElement value, CancellationToken cancellationToken)
    {
        if (key == "profile")
        {
            return value.ValueKind == JsonValueKind.String
                ? _owner.SetProfileAsync(value.GetString() ?? string.Empty, cancellationToken)
                : Task.FromResult(new SteamUiCommandResult(false, "The profile value is invalid."));
        }

        // A key names its theme, patch or component by a digest of the names, found again by
        // building the same digest over what is installed now: names may hold any character, and a
        // key stays inside the tab's bound however long they are.
        var state = _owner.ReadState();
        foreach (var theme in state.Themes)
        {
            if (key == ExtensionsKey("theme", theme.Name))
            {
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? _owner.SetEnabledAsync(theme.Name, value.GetBoolean(), cancellationToken)
                    : Task.FromResult(new SteamUiCommandResult(false, "The theme value is invalid."));
            }

            foreach (var patch in theme.Patches)
            {
                if (key == ExtensionsKey("patch", theme.Name, patch.Name))
                {
                    var option = value.ValueKind switch
                    {
                        JsonValueKind.True => "Yes",
                        JsonValueKind.False => "No",
                        JsonValueKind.Number when value.TryGetInt32(out var index) && index >= 0
                            && index < patch.Options.Count =>
                            patch.Options[index],
                        JsonValueKind.String => value.GetString(),
                        _ => null
                    };
                    return option is null
                        ? Task.FromResult(new SteamUiCommandResult(false, "The patch value is invalid."))
                        : _owner.SetPatchAsync(theme.Name, patch.Name, option, cancellationToken);
                }

                foreach (var component in patch.Components)
                {
                    if (key == ExtensionsKey("component", theme.Name, patch.Name, component.Name))
                    {
                        return value.ValueKind == JsonValueKind.String
                            ? _owner.SetComponentAsync(theme.Name, patch.Name, component.Name,
                                value.GetString() ?? string.Empty, cancellationToken)
                            : Task.FromResult(new SteamUiCommandResult(false, "The component value is invalid."));
                    }
                }
            }
        }

        return Task.FromResult(new SteamUiCommandResult(false, "That setting is no longer available."));
    }

    /// <summary>The Quick Access key of a theme, a patch or a component.</summary>
    /// <param name="kind"><c>theme</c>, <c>patch</c> or <c>component</c>.</param>
    /// <param name="names">The theme's name, then the patch's and the component's as the kind needs.</param>
    /// <returns>The kind and a digest of the names: no name's characters reach the key.</returns>
    internal static string ExtensionsKey(string kind, params string[] names)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', names)));
        return kind + ":" + Convert.ToHexString(digest, 0, 8);
    }
}
