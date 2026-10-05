using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The Steam themes in the overlay: the same service the Themes page in Steam drives, one level at a time.</summary>
/// <remarks>Renders a complete native tool over a surface-scoped browser and the shared durable service.</remarks>
public sealed partial class ThemesView : ServiceSubView
{
    private IThemeBrowseSession? _browser;
    private bool _detailOpened;

    /// <inheritdoc />
    protected override string LogScope => "Themes";

    /// <summary>Attaches the view to the session's themes, or detaches it with null.</summary>
    /// <param name="service">The themes, or null when the overlay closes or the session has none.</param>
    internal void Attach(ThemeService? service)
    {
        AttachSession(service?.CreateBrowserSession());
    }

    internal void AttachSession(IThemeBrowseSession? session)
    {
        _browser?.Dispose();
        _browser = session;
        AttachSource(session);
    }

    private static void AddStatus(StackPanel stack, SteamThemesState state)
    {
        AddStatus(stack, state.Busy, state.Error, state.Notice);
    }

    private protected override void RenderHome()
    {
        LeaveDetail();
        var stack = NewStack("CSS Loader");
        if (_browser?.ReadState() is not { } state)
        {
            stack.Children.Add(Caption("Themes are not available in this session."));
            SetContent(stack);
            return;
        }

        stack.Children.Add(ToolTabs(_browser!.Tab,
            ("browse", "Browse", () => SelectTab("browse")),
            ("installed", "Installed", () => SelectTab("installed")),
            ("profiles", "Profiles", () => SelectTab("profiles")),
            ("settings", "Settings", () => SelectTab("settings"))));
        if (_browser.Tab == "browse")
        {
            RenderBrowseInto(stack, state);
            SetContent(stack);
            return;
        }

        if (_browser.Tab == "profiles")
        {
            RenderProfilesInto(stack, state);
            SetContent(stack);
            return;
        }

        if (_browser.Tab == "settings")
        {
            RenderSettingsInto(stack, state);
            SetContent(stack);
            return;
        }

        stack.Children.Add(Caption(ThemesRows.Summary(state)));
        AddStatus(stack, state);

        stack.Children.Add(Tagged(Row("Refresh", "Read the themes folder again and check for updates", Icons.Restart,
            state.Busy ? null : () => Run(_browser!.RefreshAsync, "refresh")), "refresh"));
        if (state.Updates > 0)
        {
            stack.Children.Add(Tagged(PrimaryRow($"Update all ({state.Updates})",
                "Install every newer version the store has", Icons.ArrowDown,
                () => Run(_browser!.UpdateAllAsync, "update all")), "update-all"));
        }

        stack.Children.Add(SectionLabel("INSTALLED"));
        if (state.Themes.Count == 0)
        {
            stack.Children.Add(Caption("No themes installed. Browse themes to get started."));
        }

        foreach (var theme in state.Themes)
        {
            var name = theme.Name;
            stack.Children.Add(Tagged(Row(theme.DisplayName, ThemesRows.Describe(theme),
                theme.Enabled ? Icons.Palette : null,
                () => Navigate(() => RenderTheme(name))), "theme:" + name));
        }

        foreach (var error in state.Errors)
        {
            stack.Children.Add(Caption($"{error.Folder}: {error.Error}"));
        }


        SetContent(stack);
    }

    private void RenderTheme(string name)
    {
        LeaveDetail();
        if (_browser?.ReadState() is not { } state
            || state.Themes.FirstOrDefault(candidate => candidate.Name == name) is not { } theme)
        {
            Back();
            return;
        }

        var stack = NewStack(theme.DisplayName);
        stack.Children.Add(Caption(ThemesRows.Describe(theme)));
        AddStatus(stack, state);
        stack.Children.Add(Tagged(Row(theme.Enabled ? "On" : "Off",
            theme.Enabled ? "Press to turn the theme off" : "Press to turn the theme on", Icons.Palette,
            () => Run(token => _browser!.SetEnabledAsync(name, !theme.Enabled, token), "switch")), "enabled"));

        if (theme.Patches.Count > 0)
        {
            stack.Children.Add(SectionLabel("SETTINGS"));
        }

        foreach (var patch in theme.Patches)
        {
            var patchName = patch.Name;
            switch (patch.Type)
            {
                case "checkbox":
                    stack.Children.Add(Tagged(Row(patch.Name, patch.Value == "Yes" ? "Yes" : "No", null,
                        () => Run(token => _browser!.SetPatchAsync(name, patchName, patch.Value == "Yes" ? "No" : "Yes",
                            token), "patch")), "patch:" + patchName));
                    break;
                case "slider":
                    stack.Children.Add(ThemeSlider(name, patch));
                    break;
                case "none":
                    stack.Children.Add(Caption(patch.Name));
                    break;
                default:
                    stack.Children.Add(ChoiceRow(patch.Name,
                        [.. patch.Options.Select(option => (option, option))], patch.Value,
                        option => Run(token => _browser!.SetPatchAsync(name, patchName, option, token), "patch")));
                    break;
            }

            foreach (var component in patch.Components.Where(component => component.On == patch.Value))
            {
                var componentName = component.Name;
                stack.Children.Add(ThemeComponent(name, patchName, component));
            }
        }

        stack.Children.Add(SectionLabel("MANAGE"));
        if (theme.Status == ThemeStates.Outdated)
        {
            stack.Children.Add(Tagged(PrimaryRow($"Update to {theme.LatestVersion}",
                "Install the store's newer version", Icons.ArrowDown,
                state.Busy ? null : () => Run(token => _browser!.UpdateAsync(name, token), "update")), "update"));
        }

        stack.Children.Add(Tagged(Row(theme.Hidden ? "Show in Quick Access" : "Hide from Quick Access",
            theme.Hidden ? "Lists the theme in Steam's Quick Access again" : "Keeps the theme off Steam's Quick Access",
            Icons.Panel, () => Run(token => _browser!.SetHiddenAsync(name, !theme.Hidden, token), "hide")), "hide"));
        stack.Children.Add(Tagged(DangerRow("Delete", "Turns the theme off and removes its folder", Icons.Close,
            () =>
            {
                ConfirmCommand("Delete theme", $"Delete {theme.DisplayName}? This turns it off and removes its folder.",
                    token => _browser!.DeleteAsync(name, token));
            }), "delete"));
        SetContent(stack);
    }

    /// <summary>
    ///     Closes the detail this view opened once it shows another level, so the Themes page in
    ///     Steam does not open on a theme the overlay looked at. A detail opened in Steam is left.
    /// </summary>
    private void LeaveDetail()
    {
        if (!_detailOpened || _browser is null)
        {
            return;
        }

        _detailOpened = false;
        Run(_browser!.CloseDetailAsync, "closeDetail");
    }

    private void RenderDetail(string id)
    {
        if (_browser?.ReadState() is not { } state)
        {
            Back();
            return;
        }

        var detail = state.Detail;
        if (detail is null || detail.Item.Id != id)
        {
            _detailOpened = true;
            Run(token => _browser!.OpenAsync(id, token), "open");
            var loading = NewStack("Theme");
            loading.Children.Add(Caption("Asking the store…"));
            SetContent(loading);
            return;
        }

        var item = detail.Item;
        var stack = NewStack(item.DisplayName);
        stack.Children.Add(Caption(ThemesRows.DescribeListing(item)));
        AddStatus(stack, state);
        foreach (var url in detail.ImageUrls)
        {
            stack.Children.Add(new OverlayPreviewImage(url, 280) { Tag = "theme.screenshot:" + url });
        }

        if (detail.Loading)
        {
            stack.Children.Add(Caption("Loading the details…"));
        }
        else if (detail.Error is { } error)
        {
            stack.Children.Add(Caption(error));
        }
        else
        {
            stack.Children.Add(Caption(detail.Description.Length > 0
                ? detail.Description
                : "No description provided."));
            if (item.Targets.Count > 0)
            {
                stack.Children.Add(Caption("Targets: " + string.Join(", ", item.Targets)));
            }

            if (detail.Dependencies.Count > 0)
            {
                stack.Children.Add(Caption("Needs: " + string.Join(", ", detail.Dependencies.Select(dependency =>
                    dependency.Installed ? dependency.DisplayName : dependency.DisplayName + " (not installed)"))));
            }
        }

        var label = item.LocalStatus switch
        {
            ThemeStates.Installed => "Reinstall",
            ThemeStates.Outdated => "Update",
            _ => "Install"
        };
        stack.Children.Add(Tagged(PrimaryRow(label, "Download into the themes folder; turn it on under Installed",
                Icons.ArrowDown,
                state.Busy ? null : () => Run(token => _browser!.InstallAsync(id, token), "install")),
            "install"));

        SetContent(stack);
    }
}

/// <summary>What the overlay's Themes view says about the themes and each listing.</summary>
/// <remarks>Pure, so the wording is tested without building a window.</remarks>
internal static class ThemesRows
{
    /// <summary>The home level's summary.</summary>
    /// <param name="state">The published state.</param>
    /// <returns>How many themes are on, how many updates wait, and whether the feature is off.</returns>
    internal static string Summary(SteamThemesState state)
    {
        if (!state.Settings.Enabled)
        {
            return "Themes are off in Settings, so nothing is installed into Steam.";
        }

        if (state.Themes.Count == 0)
        {
            return "Restyle Big Picture with CSS Loader themes from DeckThemes.";
        }

        var line = $"{state.Themes.Count(theme => theme.Enabled)} of {state.Themes.Count} themes on";
        if (state.Updates > 0)
        {
            line += $", {state.Updates} update{(state.Updates == 1 ? "" : "s")} available";
        }

        return line + ".";
    }

    /// <summary>One installed theme's line.</summary>
    /// <param name="theme">The theme.</param>
    /// <returns>Whether it is on, its version and author, and whether the store has a newer one.</returns>
    internal static string Describe(SteamThemesInstalled theme)
    {
        List<string> parts = [theme.Enabled ? "On" : "Off"];
        if (theme.Status == ThemeStates.Outdated && theme.LatestVersion is { } latest)
        {
            parts.Add($"Update available ({latest})");
        }
        else
        {
            parts.Add(theme.Version);
        }

        if (theme.Author.Length > 0)
        {
            parts.Add(theme.Author);
        }

        if (theme.Hidden)
        {
            parts.Add("Hidden from Quick Access");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>One store listing's line.</summary>
    /// <param name="item">The listing.</param>
    /// <returns>Its version, author, target, downloads and whether it is installed.</returns>
    internal static string DescribeListing(SteamThemesStoreItem item)
    {
        List<string> parts = [item.Version];
        if (item.Author.Length > 0)
        {
            parts.Add(item.Author);
        }

        if (item.Target.Length > 0)
        {
            parts.Add(item.Target);
        }

        parts.Add($"{item.Downloads} downloads");
        switch (item.LocalStatus)
        {
            case ThemeStates.Installed:
                parts.Add("Installed");
                break;
            case ThemeStates.Outdated:
                parts.Add("Installed, update available");
                break;
        }

        return string.Join(" · ", parts);
    }
}
