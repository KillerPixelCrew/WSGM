using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>The boot movies in the overlay: the same service the Animations page in Steam drives, one level at a time.</summary>
/// <remarks>Renders a complete native tool over a surface-scoped browser and the shared durable service.</remarks>
public sealed partial class AnimationsView : ServiceSubView
{
    private IAnimationBrowseSession? _browser;
    private ISteamAnimationsBackend? _service;

    /// <inheritdoc />
    protected override string LogScope => "Animations";

    /// <summary>Raised when the user asks to continue on the Animations page in Steam.</summary>
    /// <summary>Attaches the view to the session's boot movies, or detaches it with null.</summary>
    /// <param name="service">The service, or null when the overlay closes or the session has none.</param>
    internal void Attach(AnimationService? service)
    {
        AttachSession(service?.CreateBrowserSession());
    }

    internal void AttachSession(IAnimationBrowseSession? session)
    {
        _browser?.Dispose();
        _browser = session;
        _service = session;
        AttachSource(session);
    }

    private protected override void RenderHome()
    {
        var stack = NewStack("Video Switcher");
        if (_browser?.ReadState() is not { } state)
        {
            stack.Children.Add(Caption("The boot movies are unavailable in this session."));
            SetContent(stack);
            return;
        }

        stack.Children.Add(ToolTabs(_browser!.Tab,
            ("browse", "Browse", () => SelectTab("browse")),
            ("library", "Library", () => SelectTab("library")),
            ("settings", "Settings", () => SelectTab("settings"))));
        if (_browser.Tab == "browse")
        {
            RenderMovieBrowse(stack, state);
            SetContent(stack);
            return;
        }

        if (_browser.Tab == "settings")
        {
            RenderMovieSettings(stack, state);
            SetContent(stack);
            return;
        }

        stack.Children.Add(Tagged(
            Row("Add a video file", "Choose a local WebM movie", Icons.ArrowDown,
                () => _ = RunSafelyAsync(AddFileAsync(), "add file")), "movie.add"));
        stack.Children.Add(Caption(AnimationsRows.Summary(state)));
        AddStatus(stack, state);

        List<(string Value, string Label)> choices = [(string.Empty, state.StockName)];
        choices.AddRange(state.Library.Select(item => (item.Id, item.Name)));
        stack.Children.Add(ChoiceRow("Boot movie", choices, state.Selected,
            id => Run(token => _service!.SelectAsync(id, token), "select")));
        stack.Children.Add(Tagged(Row("Shuffle", "Picks the boot movie anew from the library", Icons.Reorder,
            state.Library.Count == 0 ? null : () => Run(_service!.ShuffleAsync, "shuffle")), "shuffle"));
        stack.Children.Add(SectionLabel("LIBRARY"));
        if (state.Library.Count == 0)
        {
            stack.Children.Add(Caption("Nothing downloaded yet. Browse the repository to add one."));
        }

        var cards = new WrapPanel();
        foreach (var item in state.Library)
        {
            var id = item.Id;
            cards.Children.Add(PreviewCard(id, item.ThumbnailUrl, item.Name, AnimationsRows.Describe(item, state),
                () => Navigate(() => RenderMovie(id))));
        }

        stack.Children.Add(cards);

        SetContent(stack);
    }

    private void RenderDetail(string id)
    {
        RenderMovie(id);
    }

    private void RenderMovie(string id)
    {
        if (_browser?.ReadState() is not { } state)
        {
            return;
        }

        var item = state.Library.Concat(state.Browse.Items).FirstOrDefault(item => item.Id == id);
        if (item is null)
        {
            Back();
            return;
        }

        var body = NewStack(item.Name);
        AddStatus(body, state);
        AddMoviePreview(body, item);
        body.Children.Add(Caption(AnimationsRows.DescribeListing(item)));
        body.Children.Add(Caption(item.Description.Length == 0 ? "No description provided." : item.Description));
        if (item.Downloaded)
        {
            body.Children.Add(Tagged(PrimaryRow(state.Selected == id ? "Plays at boot" : "Use at boot",
                    "Changes take effect when Steam next starts", Icons.Play,
                    state.Busy || state.Selected == id ? null : () => Run(token => _service!.SelectAsync(id, token))),
                "select"));
            body.Children.Add(Tagged(DangerRow("Remove", "Delete from the library", Icons.Close,
                state.Busy
                    ? null
                    : () => ConfirmCommand("Remove movie",
                        "Remove " + item.Name + "? If selected, Steam's own movie is restored.",
                        token => _service!.DeleteAsync(id, token))), "delete"));
        }
        else
        {
            body.Children.Add(Tagged(PrimaryRow("Download", "Add to the library", Icons.ArrowDown,
                state.Busy ? null : () => Run(token => _service!.DownloadAsync(id, token))), "download"));
        }

        SetContent(body);
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
