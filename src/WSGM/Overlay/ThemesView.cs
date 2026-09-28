using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The Steam themes in the overlay: the same service the Themes page in Steam drives, one level at a time.</summary>
/// <remarks>
///     <para>
///         Renders the service's published state and calls the same methods the Steam page does, so
///         a theme switched here is what the page shows next and the other way round. The store's
///         listings are rows here; the page in Steam draws them as cards with their screenshots, and
///         the last row of every level hands over to it.
///     </para>
///     <para>
///         The service outlives this view: an install keeps running when the overlay closes, and
///         opening the view again lands on its current state.
///     </para>
/// </remarks>
public sealed class ThemesView : ServiceSubView
{
    private bool _detailOpened;
    private ThemeService? _service;

    /// <inheritdoc />
    protected override string LogScope => "Themes";

    /// <summary>Raised when the user asks to continue on the Themes page in Steam.</summary>
    internal event Action? OpenInSteamRequested;

    /// <summary>Attaches the view to the session's themes, or detaches it with null.</summary>
    /// <param name="service">The themes, or null when the overlay closes or the session has none.</param>
    internal void Attach(ThemeService? service)
    {
        _service = service;
        AttachSource(service);
    }

    private static void AddStatus(StackPanel stack, SteamThemesState state)
    {
        AddStatus(stack, state.Busy, state.Error, state.Notice);
    }

    private protected override void RenderHome()
    {
        LeaveDetail();
        var stack = NewStack("Themes");
        if (_service?.ReadState() is not { } state)
        {
            stack.Children.Add(Caption("Themes are not available in this session."));
            SetContent(stack);
            return;
        }

        stack.Children.Add(Caption(ThemesRows.Summary(state)));
        AddStatus(stack, state);

        stack.Children.Add(Tagged(Row("Browse themes", "Search DeckThemes and install", Icons.Grid4,
            () => Navigate(RenderBrowse)), "browse"));
        stack.Children.Add(Tagged(Row("Refresh", "Read the themes folder again and check for updates", Icons.Restart,
            state.Busy ? null : () => Run(_service.RefreshAsync, "refresh")), "refresh"));
        if (state.Updates > 0)
        {
            stack.Children.Add(Tagged(PrimaryRow($"Update all ({state.Updates})",
                "Install every newer version the store has", Icons.ArrowDown,
                () => Run(_service.UpdateAllAsync, "update all")), "update-all"));
        }

        if (state.Presets.Count > 0)
        {
            stack.Children.Add(SectionLabel("PROFILE"));
            List<(string Value, string Label)> options = [(string.Empty, "None")];
            options.AddRange(state.Presets.Select(preset => (preset.Name, preset.DisplayName)));
            stack.Children.Add(ChoiceRow("Selected profile", options, state.SelectedPreset,
                name => Run(token => _service.SetProfileAsync(name, token), "profile")));
        }

        if (state.Themes.Any(theme => theme.Enabled))
        {
            stack.Children.Add(Tagged(Row("Save as profile…",
                    "Combines every enabled theme and its settings under one name", Icons.CopyDoc,
                    () => EditText("Profile name", string.Empty, 64,
                        name => Run(token => _service.CreateProfileAsync(name, token), "create profile"))),
                "create-profile"));
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

        stack.Children.Add(OpenInSteamRow("Browse with screenshots on the Themes page",
            () => OpenInSteamRequested?.Invoke()));
        SetContent(stack);
    }

    private void RenderTheme(string name)
    {
        LeaveDetail();
        if (_service?.ReadState() is not { } state
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
            () => Run(token => _service.SetEnabledAsync(name, !theme.Enabled, token), "switch")), "enabled"));

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
                        () => Run(token => _service.SetPatchAsync(name, patchName, patch.Value == "Yes" ? "No" : "Yes",
                            token), "patch")), "patch:" + patchName));
                    break;
                case "none":
                    break;
                default:
                    stack.Children.Add(ChoiceRow(patch.Name,
                        [.. patch.Options.Select(option => (option, option))], patch.Value,
                        option => Run(token => _service.SetPatchAsync(name, patchName, option, token), "patch")));
                    break;
            }

            foreach (var component in patch.Components.Where(component => component.On == patch.Value))
            {
                var componentName = component.Name;
                stack.Children.Add(Tagged(Row(component.Name, component.Value,
                        component.Type == "color-picker" ? Icons.Palette : Icons.CopyDoc,
                        () => EditText(component.Name, component.Value, 256,
                            value => Run(
                                token => _service.SetComponentAsync(name, patchName, componentName, value, token),
                                "component"))), $"component:{patchName}:{componentName}"));
            }
        }

        stack.Children.Add(SectionLabel("MANAGE"));
        if (theme.Status == "outdated")
        {
            stack.Children.Add(Tagged(PrimaryRow($"Update to {theme.LatestVersion}",
                "Install the store's newer version", Icons.ArrowDown,
                state.Busy ? () => { } : () => Run(token => _service.UpdateAsync(name, token), "update")), "update"));
        }

        stack.Children.Add(Tagged(Row(theme.Hidden ? "Show in Quick Access" : "Hide from Quick Access",
            theme.Hidden ? "Lists the theme in Steam's Quick Access again" : "Keeps the theme off Steam's Quick Access",
            Icons.Panel, () => Run(token => _service.SetHiddenAsync(name, !theme.Hidden, token), "hide")), "hide"));
        stack.Children.Add(Tagged(DangerRow("Delete", "Turns the theme off and removes its folder", Icons.Close,
            () =>
            {
                Run(token => _service.DeleteAsync(name, token), "delete");
                Back();
            }), "delete"));
        SetContent(stack);
    }

    private void RenderBrowse()
    {
        LeaveDetail();
        var stack = NewStack("Browse");
        if (_service?.ReadState() is not { } state)
        {
            SetContent(stack);
            return;
        }

        var browse = state.Browse;
        if (browse.Items.Count == 0 && !browse.Loading && browse.Error is null && browse.Page == 0)
        {
            Run(token => _service.BrowseAsync(browse.Filter, browse.Order, browse.Search, token), "browse");
        }

        AddStatus(stack, state);
        stack.Children.Add(Tagged(Row(browse.Search.Length > 0 ? $"Search: {browse.Search}" : "Search",
                "Press to type", Icons.ListLines,
                () => EditText("Search themes", browse.Search, 64,
                    text => Run(token => _service.BrowseAsync(browse.Filter, browse.Order, text, token), "browse"))),
            "search"));
        List<(string Value, string Label)> filters = [(ThemeStoreQuery.AllFilter, "All")];
        filters.AddRange(browse.Filters.Where(pair => pair.Value > 0)
            .Select(pair => (pair.Key, $"{pair.Key} ({pair.Value})")));
        stack.Children.Add(ChoiceRow("Filter", filters, browse.Filter,
            filter => Run(token => _service.BrowseAsync(filter, browse.Order, browse.Search, token), "browse")));
        stack.Children.Add(ChoiceRow("Sort", [.. browse.Orders.Select(order => (order, order))], browse.Order,
            order => Run(token => _service.BrowseAsync(browse.Filter, order, browse.Search, token), "browse")));

        stack.Children.Add(SectionLabel(browse.Total > 0 ? $"{browse.Total} THEMES" : "THEMES"));
        if (browse.Loading && browse.Items.Count == 0)
        {
            stack.Children.Add(Caption("Asking the store…"));
        }
        else if (browse.Error is { } error)
        {
            stack.Children.Add(Caption(error));
        }
        else if (browse.Items.Count == 0)
        {
            stack.Children.Add(Caption("Nothing matched."));
        }

        foreach (var item in browse.Items)
        {
            var id = item.Id;
            stack.Children.Add(Tagged(Row(item.DisplayName, ThemesRows.DescribeListing(item),
                item.LocalStatus == "none" ? null : Icons.Palette,
                () => Navigate(() => RenderDetail(id))), "store:" + id));
        }

        if (browse.Items.Count < browse.Total)
        {
            stack.Children.Add(Tagged(Row("Load more", $"{browse.Items.Count} of {browse.Total} listed",
                Icons.ArrowDown,
                browse.Loading ? null : () => Run(_service.LoadMoreAsync, "load more")), "load-more"));
        }

        stack.Children.Add(OpenInSteamRow("Browse with screenshots on the Themes page",
            () => OpenInSteamRequested?.Invoke()));
        SetContent(stack);
    }

    /// <summary>
    ///     Closes the detail this view opened once it shows another level, so the Themes page in
    ///     Steam does not open on a theme the overlay looked at. A detail opened in Steam is left.
    /// </summary>
    private void LeaveDetail()
    {
        if (!_detailOpened || _service is null)
        {
            return;
        }

        _detailOpened = false;
        Run(_service.CloseDetailAsync, "closeDetail");
    }

    private void RenderDetail(string id)
    {
        if (_service?.ReadState() is not { } state)
        {
            Back();
            return;
        }

        var detail = state.Detail;
        if (detail is null || detail.Item.Id != id)
        {
            _detailOpened = true;
            Run(token => _service.OpenAsync(id, token), "open");
            var loading = NewStack("Theme");
            loading.Children.Add(Caption("Asking the store…"));
            SetContent(loading);
            return;
        }

        var item = detail.Item;
        var stack = NewStack(item.DisplayName);
        stack.Children.Add(Caption(ThemesRows.DescribeListing(item)));
        AddStatus(stack, state);
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
            "installed" => "Reinstall",
            "outdated" => "Update",
            _ => "Install"
        };
        stack.Children.Add(Tagged(PrimaryRow(label, "Download into the themes folder; turn it on under Installed",
                Icons.ArrowDown,
                state.Busy ? () => { } : () => Run(token => _service.InstallAsync(id, token), "install")),
            "install"));
        stack.Children.Add(OpenInSteamRow("See the screenshots on the Themes page",
            () => OpenInSteamRequested?.Invoke()));
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
        if (theme.Status == "outdated" && theme.LatestVersion is { } latest)
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
            case "installed":
                parts.Add("Installed");
                break;
            case "outdated":
                parts.Add("Installed, update available");
                break;
        }

        return string.Join(" · ", parts);
    }
}
