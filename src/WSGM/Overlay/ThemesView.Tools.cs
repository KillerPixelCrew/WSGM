using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

public sealed partial class ThemesView
{
    private void SelectTab(string tab)
    {
        _browser!.Tab = tab;
        _browser.CancelQueries();
        Replace(RenderHome);
    }

    internal override void Leave()
    {
        _browser?.CancelQueries();
        base.Leave();
    }

    private void RenderBrowseInto(StackPanel body, SteamThemesState state)
    {
        var browse = state.Browse;
        AddStatus(body, state);
        body.Children.Add(Tagged(Row("Search", browse.Search, Icons.ListLines, () => EditText("Search themes",
                browse.Search, 64,
                text => Run(token => _browser!.BrowseAsync(browse.Filter, browse.Order, text, token), "browse"))),
            "search"));
        List<(string Value, string Label)> filters = [("All", "All")];
        filters.AddRange(browse.Filters.Where(pair => pair.Value > 0)
            .Select(pair => (pair.Key, $"{pair.Key} ({pair.Value})")));
        body.Children.Add(ChoiceRow("Target", filters, browse.Filter,
            filter => Run(token => _browser!.BrowseAsync(filter, browse.Order, browse.Search, token))));
        body.Children.Add(ChoiceRow("Sort", browse.Orders.Select(order => (order, order)).ToArray(), browse.Order,
            order => Run(token => _browser!.BrowseAsync(browse.Filter, order, browse.Search, token))));
        if (browse.Page == 0 && !browse.Loading && browse.Error is null)
        {
            Run(token => _browser!.BrowseAsync(browse.Filter, browse.Order, browse.Search, token), "browse");
        }

        if (browse.Error is not null)
        {
            body.Children.Add(Caption(browse.Error));
            body.Children.Add(Tagged(Row("Retry", "", Icons.Restart,
                    () => Run(token => _browser!.BrowseAsync(browse.Filter, browse.Order, browse.Search, token))),
                "retry"));
        }

        var cards = new WrapPanel();
        foreach (var item in browse.Items)
        {
            var id = item.Id;
            cards.Children.Add(PreviewCard(id, item.ImageUrl, item.DisplayName, ThemesRows.DescribeListing(item),
                () => Navigate(() => RenderDetail(id))));
        }

        body.Children.Add(cards);
        if (!browse.Loading && browse.Items.Count == 0 && browse.Error is null)
        {
            body.Children.Add(Caption("No matching themes."));
        }

        if (browse.Items.Count < browse.Total)
        {
            body.Children.Add(Tagged(Row("Load more", $"{browse.Items.Count} of {browse.Total}", Icons.ArrowDown,
                browse.Loading ? null : () => Run(_browser!.LoadMoreAsync)), "load-more"));
        }
    }

    private void RenderProfilesInto(StackPanel body, SteamThemesState state)
    {
        AddStatus(body, state);
        List<(string Value, string Label)> options = [("", "None")];
        if (state.SelectedPreset == "Invalid State")
        {
            options.Add(("Invalid State", "Invalid State"));
        }

        options.AddRange(state.Presets.Select(profile => (profile.Name, profile.DisplayName)));
        body.Children.Add(ChoiceRow("Selected profile", options, state.SelectedPreset,
            name => Run(token => _service!.SetProfileAsync(name, token))));
        body.Children.Add(Tagged(Row("Create profile", "Save the enabled themes and their settings", Icons.CopyDoc,
            state.Busy
                ? null
                : () => EditText("Profile name", "", 64,
                    name => Run(token => _service!.CreateProfileAsync(name, token)))), "profiles.create"));
        foreach (var profile in state.Presets)
        {
            var name = profile.Name;
            body.Children.Add(Caption(profile.DisplayName + ": " + string.Join(", ", profile.Dependencies)));
            body.Children.Add(Tagged(DangerRow("Delete " + profile.DisplayName, "Remove the saved profile", Icons.Close,
                state.Busy
                    ? null
                    : () => ConfirmCommand("Delete profile", "Delete " + profile.DisplayName + "?",
                        token => _service!.DeleteAsync(name, token))), "profile.delete:" + name));
        }
    }

    private void RenderSettingsInto(StackPanel body, SteamThemesState state)
    {
        AddStatus(body, state);
        var settings = state.Settings;
        body.Children.Add(ToggleRow("Install themes into Steam", settings.Enabled,
            enabled => Run(token =>
                _service!.SetSettingAsync("enabled", JsonSerializer.SerializeToElement(enabled), token))));
        body.Children.Add(ChoiceRow("Class translations",
            new[]
            {
                ("auto", settings.SteamBeta ? "Auto (beta)" : "Auto (stable)"), ("stable", "Force Stable"),
                ("beta", "Force Beta")
            },
            settings.TranslationsBranch,
            value => Run(token =>
                _service!.SetSettingAsync("translationsBranch", JsonSerializer.SerializeToElement(value), token))));
        body.Children.Add(Caption(
            $"Translations: {settings.Translations} names; fetched {settings.TranslationsFetched ?? "not yet"}"));
        body.Children.Add(Caption("Themes folder: " + settings.ThemesPath));
        body.Children.Add(Caption("Steam themes_custom: " + settings.SteamLink));
        body.Children.Add(Tagged(
            Row("Refresh", "Reload themes and check updates", Icons.Restart,
                state.Busy ? null : () => Run(_service!.RefreshAsync)), "refresh"));
        body.Children.Add(Tagged(Row("Dismiss notice", "", Icons.Close, () => Run(_service!.DismissAsync)), "dismiss"));
    }

    private Control ThemeSlider(string name, ThemePatchSnapshot patch)
    {
        var body = new StackPanel { Spacing = 4 };
        var slider = new DeviceSliderRow("patch:" + name + ":" + patch.Name, patch.Name, patch.Value,
            0, Math.Max(0, patch.Options.Count - 1), 1, CapabilityUnit.None,
            Math.Max(0, patch.Options.ToList().IndexOf(patch.Value)), patch.Options.Count > 0,
            index =>
            {
                if (index >= 0 && index < patch.Options.Count)
                {
                    Run(token => _service!.SetPatchAsync(name, patch.Name, patch.Options[index], token));
                }
            });
        body.Children.Add(slider);
        return body;
    }

    private Control ThemeComponent(string theme, string patch, ThemeComponentSnapshot component)
    {
        var body = new StackPanel { Spacing = 6, Tag = "component:" + theme + ":" + patch + ":" + component.Name };
        body.Children.Add(Caption(component.Name));
        if (component.Type == "color-picker" && TryThemeColor(component.Value, out var color))
        {
            var picker = new OverlayColorPicker
            {
                Color = color, IsAlphaEnabled = true,
                Tag = "component.color:" + theme + ":" + patch + ":" + component.Name
            };
            picker.Commit = chosen => Run(token =>
                _service!.SetComponentAsync(theme, patch, component.Name, ThemeColorText(chosen), token));
            body.Children.Add(picker);
        }

        body.Children.Add(Tagged(Row("Edit value", component.Value, Icons.CopyDoc,
                () => EditText(component.Name, component.Value, 256,
                    value => Run(token => _service!.SetComponentAsync(theme, patch, component.Name, value, token)))),
            "component.value:" + theme + ":" + patch + ":" + component.Name));
        return body;
    }

    private static bool TryThemeColor(string value, out Color color)
    {
        if (value.StartsWith('#') && value.Length is 4 or 5)
        {
            value = "#" + string.Concat(value.Skip(1).Select(character => new string(character, 2)));
        }

        if (value.StartsWith('#') && value.Length == 9)
        {
            value = "#" + value[7..9] + value[1..7];
        }

        return Color.TryParse(value, out color);
    }

    private static string ThemeColorText(Color color)
    {
        return color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
    }
}
