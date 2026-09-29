using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The boot movies in the overlay: the same service the Animations page in Steam drives, one level at a time.</summary>
/// <remarks>
///     Rows rather than cards, and no preview: the overlay hands over to the page in Steam for those.
///     The choice, the library, the shuffle and the repository's list are all here.
/// </remarks>
public sealed class AnimationsView : ServiceSubView
{
    private AnimationService? _service;

    /// <inheritdoc />
    protected override string LogScope => "Animations";

    /// <summary>Raised when the user asks to continue on the Animations page in Steam.</summary>
    internal event Action? OpenInSteamRequested;

    /// <summary>Attaches the view to the session's boot movies, or detaches it with null.</summary>
    /// <param name="service">The service, or null when the overlay closes or the session has none.</param>
    internal void Attach(AnimationService? service)
    {
        _service = service;
        AttachSource(service);
    }

    private protected override void RenderHome()
    {
        var stack = NewStack("Boot animation");
        if (_service?.ReadState() is not { } state)
        {
            stack.Children.Add(Caption("The boot movies are unavailable in this session."));
            SetContent(stack);
            return;
        }

        stack.Children.Add(Caption(AnimationsRows.Summary(state)));
        AddStatus(stack, state);

        List<(string Value, string Label)> choices = [(string.Empty, state.StockName)];
        choices.AddRange(state.Library.Select(item => (item.Id, item.Name)));
        stack.Children.Add(ChoiceRow("Boot movie", choices, state.Selected,
            id => Run(token => _service.SelectAsync(id, token), "select")));
        stack.Children.Add(Tagged(Row("Shuffle", "Picks the boot movie anew from the library", Icons.Reorder,
            state.Library.Count == 0 ? null : () => Run(_service.ShuffleAsync, "shuffle")), "shuffle"));
        stack.Children.Add(Tagged(Row(state.Settings.ShuffleOnStart ? "Shuffle on start: on" : "Shuffle on start: off",
                "Picks the boot movie anew each time WSGM starts", Icons.Restart,
                () => Run(token => _service.SetShuffleOnStartAsync(!state.Settings.ShuffleOnStart, token),
                    "setting")),
            "shuffle-on-start"));
        stack.Children.Add(new DeviceSliderRow("boot-volume", "Volume",
            "How loud the boot movie plays, against its file; Opus movies only", 0, 100, 5, CapabilityUnit.Percent,
            state.Settings.BootVolume, true,
            volume => Run(token => _service.SetBootVolumeAsync(volume, token), "setting")));

        stack.Children.Add(SectionLabel("LIBRARY"));
        if (state.Library.Count == 0)
        {
            stack.Children.Add(Caption("Nothing downloaded yet. Browse the repository to add one."));
        }

        foreach (var item in state.Library)
        {
            var id = item.Id;
            stack.Children.Add(Tagged(Row(item.Name, AnimationsRows.Describe(item, state), Icons.Play,
                () => Navigate(() => RenderEntry(id))), "library:" + id));
        }

        stack.Children.Add(Tagged(Row("Browse the repository", "SteamDeckRepo's boot movies",
            Icons.ArrowDown, () => Navigate(RenderBrowse)), "browse"));
        stack.Children.Add(OpenInSteamRow("Browse with previews on the Animations page",
            () => OpenInSteamRequested?.Invoke()));
        SetContent(stack);
    }

    private void RenderEntry(string id)
    {
        if (_service?.ReadState() is not { } state
            || state.Library.FirstOrDefault(candidate => candidate.Id == id) is not { } item)
        {
            Back();
            return;
        }

        var stack = NewStack(item.Name);
        stack.Children.Add(Caption(AnimationsRows.Describe(item, state)));
        AddStatus(stack, state);
        if (item.Description.Length > 0)
        {
            stack.Children.Add(Caption(item.Description));
        }

        var playing = state.Selected == id;
        stack.Children.Add(Tagged(PrimaryRow(playing ? "Plays at boot" : "Start Big Picture with it",
            playing ? "Big Picture starts with this movie" : "Takes effect at the next Steam start", Icons.Play,
            playing ? () => { } : () => Run(token => _service.SelectAsync(id, token), "select")), "select"));
        stack.Children.Add(Tagged(DangerRow("Remove", "Deletes the movie from the library", Icons.Close, () =>
        {
            Run(token => _service.DeleteAsync(id, token), "delete");
            Back();
        }), "delete"));
        SetContent(stack);
    }

    private void RenderBrowse()
    {
        var stack = NewStack("Browse");
        if (_service?.ReadState() is not { } state)
        {
            SetContent(stack);
            return;
        }

        var browse = state.Browse;
        if (browse.Total == 0 && !browse.Loading && browse.Error is null)
        {
            Run(token => _service.BrowseAsync(browse.Sort, browse.Search, token), "browse");
        }

        AddStatus(stack, state);
        stack.Children.Add(Tagged(Row(browse.Search.Length > 0 ? $"Search: {browse.Search}" : "Search",
                "Press to type", Icons.ListLines,
                () => EditText("Search movies", browse.Search, 64,
                    text => Run(token => _service.BrowseAsync(browse.Sort, text, token), "browse"))),
            "search"));
        stack.Children.Add(ChoiceRow("Sort", [.. browse.Sorts.Select(sort => (sort.Id, sort.Label))],
            browse.Sort, sort => Run(token => _service.BrowseAsync(sort, browse.Search, token), "browse")));

        stack.Children.Add(SectionLabel(browse.Matched > 0 ? $"{browse.Matched} MOVIES" : "MOVIES"));
        if (browse.Loading && browse.Items.Count == 0)
        {
            stack.Children.Add(Caption("Asking the repository…"));
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
            stack.Children.Add(Tagged(Row(item.Name, AnimationsRows.DescribeListing(item),
                item.Downloaded ? Icons.Play : null,
                () => Navigate(() => RenderDetail(id))), "repo:" + id));
        }

        if (browse.Items.Count < browse.Matched)
        {
            stack.Children.Add(Tagged(Row("Show more", $"{browse.Items.Count} of {browse.Matched} shown",
                Icons.ArrowDown, () => Run(_service.BrowseMoreAsync, "more")), "more"));
        }

        stack.Children.Add(Tagged(Row("Refresh", "Asks the repository again", Icons.Restart,
            browse.Loading ? null : () => Run(_service.RefreshAsync, "refresh")), "refresh"));
        stack.Children.Add(OpenInSteamRow("Browse with previews on the Animations page",
            () => OpenInSteamRequested?.Invoke()));
        SetContent(stack);
    }

    private void RenderDetail(string id)
    {
        if (_service?.ReadState() is not { } state
            || state.Browse.Items.FirstOrDefault(candidate => candidate.Id == id) is not { } item)
        {
            Back();
            return;
        }

        var stack = NewStack(item.Name);
        stack.Children.Add(Caption(AnimationsRows.DescribeListing(item)));
        AddStatus(stack, state);
        stack.Children.Add(Caption(item.Description.Length > 0 ? item.Description : "No description provided."));
        stack.Children.Add(Tagged(PrimaryRow(item.Downloaded ? "Downloaded" : "Download",
                item.Downloaded ? "In the library; choose it there" : "Adds the movie to the library",
                Icons.ArrowDown,
                state.Busy || item.Downloaded
                    ? () => { }
                    : () => Run(token => _service.DownloadAsync(id, token), "download")),
            "download"));
        stack.Children.Add(OpenInSteamRow("See the preview on the Animations page",
            () => OpenInSteamRequested?.Invoke()));
        SetContent(stack);
    }

    private static void AddStatus(StackPanel stack, SteamAnimationsState state)
    {
        AddStatus(stack, state.Busy, state.Error, state.Notice);
    }
}

/// <summary>What the overlay's Animations view says about the choice and each movie.</summary>
/// <remarks>Pure, so the wording is tested without building a window.</remarks>
internal static class AnimationsRows
{
    /// <summary>The home level's summary.</summary>
    /// <param name="state">The published state.</param>
    /// <returns>Which movie Big Picture starts with, and whether Steam has to restart to show it.</returns>
    internal static string Summary(SteamAnimationsState state)
    {
        var selected = state.Library.FirstOrDefault(item => item.Id == state.Selected);
        var line = selected is null
            ? "Big Picture starts with Steam's own movie."
            : $"Big Picture starts with {selected.Name}.";
        if (state.Settings.RestartNeeded)
        {
            line += " Restart Steam to see the change.";
        }

        return line;
    }

    /// <summary>One library entry's line.</summary>
    /// <param name="item">The entry.</param>
    /// <param name="state">The published state, for whether it plays.</param>
    /// <returns>Whether it is the user's file, its author, and whether it plays at boot.</returns>
    internal static string Describe(SteamAnimationsItem item, SteamAnimationsState state)
    {
        List<string> parts = [item.Custom ? "Your file" : "SteamDeckRepo"];
        if (item.Author.Length > 0)
        {
            parts.Add(item.Author);
        }

        if (state.Selected == item.Id)
        {
            parts.Add("Plays at boot");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>One repository listing's line.</summary>
    /// <param name="item">The listing.</param>
    /// <returns>Its author, date, likes, downloads and whether the library holds it.</returns>
    internal static string DescribeListing(SteamAnimationsItem item)
    {
        List<string> parts = [];
        if (item.Author.Length > 0)
        {
            parts.Add(item.Author);
        }

        if (item.Updated.Length > 0)
        {
            parts.Add(item.Updated);
        }

        parts.Add($"{item.Likes} likes");
        parts.Add($"{item.Downloads} downloads");
        if (item.Downloaded)
        {
            parts.Add("In the library");
        }

        return string.Join(" · ", parts);
    }
}
