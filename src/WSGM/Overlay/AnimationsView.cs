using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The standby animations in the overlay: the same service the Animations page in Steam drives, one level at a
///     time.
/// </summary>
/// <remarks>
///     Rows rather than cards, and no preview: the overlay hands over to the page in Steam for
///     those. The slots, the library, the shuffle and the repository's list are all here.
/// </remarks>
public sealed class AnimationsView : ServiceSubView
{
    private AnimationService? _service;

    /// <inheritdoc />
    protected override string LogScope => "Animations";

    /// <summary>Raised when the user asks to continue on the Animations page in Steam.</summary>
    internal event Action? OpenInSteamRequested;

    /// <summary>Attaches the view to the session's animations, or detaches it with null.</summary>
    /// <param name="service">The animations, or null when the overlay closes or the session has none.</param>
    internal void Attach(AnimationService? service)
    {
        _service = service;
        AttachSource(service);
    }

    private protected override void RenderHome()
    {
        var stack = NewStack("Animations");
        if (_service?.ReadState() is not { } state)
        {
            stack.Children.Add(Caption("The animations are unavailable in this session."));
            SetContent(stack);
            return;
        }

        stack.Children.Add(Caption(AnimationsRows.Summary(state)));
        AddStatus(stack, state);

        stack.Children.Add(SectionLabel("SLOTS"));
        foreach (var slot in AnimationSlots.All)
        {
            List<(string Value, string Label)> choices = [(string.Empty, AnimationService.StockLabel)];
            choices.AddRange(state.Library.Where(item => AnimationTargets.Fits(item.Target, slot))
                .Select(item => (item.Id, item.Name)));
            var current = state.Library.Any(item => item.Id == state.Slots[slot]) ? state.Slots[slot] : string.Empty;
            var chosen = slot;
            stack.Children.Add(ChoiceRow(AnimationSlots.Label(slot), choices, current,
                id => Run(token => _service.SetSlotAsync(chosen, id, token), "slot")));
        }

        stack.Children.Add(Tagged(Row("Shuffle", "Picks every slot anew from the library", Icons.Reorder,
            state.Library.Count == 0 ? null : () => Run(_service.ShuffleAsync, "shuffle")), "shuffle"));
        stack.Children.Add(Tagged(Row(state.Settings.ShuffleOnStart ? "Shuffle on start: on" : "Shuffle on start: off",
            "Picks every slot anew each time WSGM starts", Icons.Restart,
            () => Run(token => _service.SetSettingAsync("shuffleOnStart",
                    JsonSerializer.SerializeToElement(!state.Settings.ShuffleOnStart), token),
                "setting")), "shuffle-on-start"));

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

        stack.Children.Add(Tagged(Row("Browse the repository", "SteamDeckRepo's boot and suspend movies",
            Icons.ArrowDown, () => Navigate(RenderBrowse)), "browse"));
        stack.Children.Add(Tagged(Row("Open in Steam", "Browse with previews on the Animations page",
            Icons.SteamLike, () => OpenInSteamRequested?.Invoke()), "open-in-steam"));
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

        stack.Children.Add(SectionLabel("USE FOR"));
        foreach (var slot in AnimationSlots.All.Where(slot => AnimationTargets.Fits(item.Target, slot)))
        {
            var chosen = slot;
            var plays = state.Slots[slot] == id;
            stack.Children.Add(Tagged(Row(AnimationSlots.Label(slot),
                plays ? "Plays this animation" : "Press to play this animation here", plays ? Icons.Play : null,
                plays ? null : () => Run(token => _service.SetSlotAsync(chosen, id, token), "slot")), "slot:" + slot));
        }

        stack.Children.Add(SectionLabel("MANAGE"));
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
            Run(token => _service.BrowseAsync(browse.Type, browse.Sort, browse.Search, token), "browse");
        }

        AddStatus(stack, state);
        stack.Children.Add(Tagged(Row(browse.Search.Length > 0 ? $"Search: {browse.Search}" : "Search",
                "Press to type", Icons.ListLines,
                () => EditText("Search animations", browse.Search, 64,
                    text => Run(token => _service.BrowseAsync(browse.Type, browse.Sort, text, token), "browse"))),
            "search"));
        stack.Children.Add(ChoiceRow("Type",
            [("all", "All"), (AnimationTargets.Boot, "Boot"), (AnimationTargets.Suspend, "Suspend")],
            browse.Type,
            type => Run(token => _service.BrowseAsync(type, browse.Sort, browse.Search, token), "browse")));
        stack.Children.Add(ChoiceRow("Sort", [.. SteamAnimationsSurface.Sorts.Select(sort => (sort, sort))],
            browse.Sort,
            sort => Run(token => _service.BrowseAsync(browse.Type, sort, browse.Search, token), "browse")));

        stack.Children.Add(SectionLabel(browse.Items.Count > 0 ? $"{browse.Items.Count} ANIMATIONS" : "ANIMATIONS"));
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

        stack.Children.Add(Tagged(Row("Refresh", "Asks the repository again", Icons.Restart,
            browse.Loading ? null : () => Run(_service.RefreshAsync, "refresh")), "refresh"));
        stack.Children.Add(Tagged(Row("Open in Steam", "Browse with previews on the Animations page",
            Icons.SteamLike, () => OpenInSteamRequested?.Invoke()), "open-in-steam"));
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
                item.Downloaded ? "In the library; choose a slot for it there" : "Adds the movie to the library",
                Icons.ArrowDown,
                state.Busy || item.Downloaded
                    ? () => { }
                    : () => Run(token => _service.DownloadAsync(id, token), "download")),
            "download"));
        stack.Children.Add(Tagged(Row("Open in Steam", "See the preview on the Animations page", Icons.SteamLike,
            () => OpenInSteamRequested?.Invoke()), "open-in-steam"));
        SetContent(stack);
    }

    private static void AddStatus(StackPanel stack, SteamAnimationsState state)
    {
        if (state.Busy)
        {
            stack.Children.Add(Caption("Working…"));
        }

        if (state.Error is { Length: > 0 } error)
        {
            stack.Children.Add(Caption(error));
        }
        else if (state.Notice is { Length: > 0 } notice)
        {
            stack.Children.Add(Caption(notice));
        }
    }
}

/// <summary>What the overlay's Animations view says about the slots and each animation.</summary>
/// <remarks>Pure, so the wording is tested without building a window.</remarks>
internal static class AnimationsRows
{
    /// <summary>The home level's summary.</summary>
    /// <param name="state">The published state.</param>
    /// <returns>Which slots play a movie of WSGM's, and whether Steam has to restart to show it.</returns>
    internal static string Summary(SteamAnimationsState state)
    {
        var playing = AnimationSlots.All.Count(slot => state.Slots[slot].Length > 0);
        var line = playing == 0
            ? "Steam plays its own boot and suspend movies."
            : $"{playing} of {AnimationSlots.All.Count} slots play a movie from the library.";
        if (state.Settings.RestartNeeded)
        {
            line += " Restart Steam to see the change.";
        }

        return line;
    }

    /// <summary>One library entry's line.</summary>
    /// <param name="item">The entry.</param>
    /// <param name="state">The published state, for the slots it plays in.</param>
    /// <returns>Its kind, its author, and the slots it plays in.</returns>
    internal static string Describe(SteamAnimationsItem item, SteamAnimationsState state)
    {
        List<string> parts = [item.Custom ? "Your file" : item.Target == AnimationTargets.Boot ? "Boot" : "Suspend"];
        if (item.Author.Length > 0)
        {
            parts.Add(item.Author);
        }

        var slots = AnimationSlots.All.Where(slot => state.Slots[slot] == item.Id).Select(AnimationSlots.Label)
            .ToList();
        if (slots.Count > 0)
        {
            parts.Add("Plays: " + string.Join(", ", slots));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>One repository listing's line.</summary>
    /// <param name="item">The listing.</param>
    /// <returns>Its kind, author, date, likes, downloads and whether the library holds it.</returns>
    internal static string DescribeListing(SteamAnimationsItem item)
    {
        List<string> parts = [item.Target == AnimationTargets.Boot ? "Boot" : "Suspend"];
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
