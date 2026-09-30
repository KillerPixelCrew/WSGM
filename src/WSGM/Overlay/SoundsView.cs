using System.Linq;
using Avalonia.Controls;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>Controller-friendly sound-pack selection, installation and independent asset previews.</summary>
public sealed class SoundsView : ServiceSubView
{
    private string _search = "";
    private SoundPackService? _service;
    /// <inheritdoc />
    protected override string LogScope => "Sounds";

    internal void Attach(SoundPackService? service)
    {
        if (service is null && _service is { } previous)
        {
            Run(previous.StopPreviewAsync, "stop preview");
        }

        _service = service;
        AttachSource(service);
    }

    internal void StopPreview()
    {
        if (_service is { } service)
        {
            Run(service.StopPreviewAsync, "stop preview");
        }
    }

    private protected override void RenderHome()
    {
        var stack = NewStack("Steam UI sounds");
        if (_service is not { } service)
        {
            stack.Children.Add(Caption("Sound packs are unavailable in this session."));
            SetContent(stack);
            return;
        }

        var state = service.ReadState();
        AddStatus(stack, state.Busy, state.Error, null);
        stack.Children.Add(Caption(state.Compatibility));
        stack.Children.Add(Caption(state.Integration));
        stack.Children.Add(Tagged(Row("Restore Steam defaults",
            state.Selected.Length == 0 ? "Selected" : "Stop overriding UI sounds",
            Icons.Restart, () => Run(token => service.SelectAsync("", token), "restore")), "sounds.defaults"));
        stack.Children.Add(Tagged(Row("Browse sound packs", "Audio Loader packs from DeckThemes", Icons.ListLines, () =>
        {
            Navigate(RenderBrowse);
            Run(token => service.BrowseAsync(1, _search, token), "browse");
        }), "sounds.browse"));
        stack.Children.Add(Tagged(Row("Import ZIP", "Install or update a local Audio Loader pack", Icons.ArrowDown,
            () => EditText("Sound-pack ZIP path", "", 1024,
                path => Run(token => service.ImportAsync(path, token), "import"))), "sounds.import"));
        stack.Children.Add(Tagged(Row("Refresh", "Read installed packs and the current Steam resources", Icons.Restart,
            () => Run(service.RefreshAsync, "refresh")), "sounds.refresh"));
        var library = new StackPanel { Spacing = 8 };
        foreach (var pack in state.Packs)
        {
            var id = pack.Id;
            library.Children.Add(Tagged(Row(pack.Name, pack.Error ?? string.Join(" · ", new[]
                        { state.Selected == id ? "Selected" : "", pack.Author, pack.Version }
                    .Where(text => text.Length > 0)),
                Icons.Play, () => Navigate(() => RenderPack(id))), "sounds.pack." + id));
        }

        if (state.Packs.Length == 0)
        {
            library.Children.Add(Caption("No sound packs installed."));
        }

        stack.Children.Add(new CollapsibleSection("Installed packs", library));
        SetContent(stack);
    }

    private void RenderPack(string id)
    {
        if (_service is not { } service || service.ReadState().Packs.FirstOrDefault(pack => pack.Id == id) is not
                { } pack)
        {
            Back();
            return;
        }

        var state = service.ReadState();
        var stack = NewStack(pack.Name);
        AddStatus(stack, state.Busy, state.Error ?? pack.Error, null);
        stack.Children.Add(Caption(pack.Description));
        if (pack.Error is null)
        {
            stack.Children.Add(Tagged(PrimaryRow(state.Selected == id ? "Selected" : "Activate",
                "Missing or incompatible sounds keep Steam defaults", Icons.Play,
                () => Run(token => service.SelectAsync(id, token), "activate")), "sounds.activate"));
            var previews = new StackPanel { Spacing = 8 };
            foreach (var asset in service.PreviewAssets(pack))
            {
                var file = asset;
                previews.Children.Add(Tagged(Row(file, "Preview this pack asset", Icons.Play,
                    () => Run(token => service.PreviewAsync(id, file, token), "preview")), "sounds.preview." + file));
            }

            previews.Children.Add(Row("Stop preview", "", Icons.Close,
                () => Run(service.StopPreviewAsync, "stop preview")));
            stack.Children.Add(new CollapsibleSection("Preview sounds", previews));
        }

        if (pack.StoreId is { Length: > 0 } storeId)
        {
            stack.Children.Add(Tagged(Row("Update from repository", "Download the current version", Icons.ArrowDown,
                () => Run(token => service.InstallAsync(storeId, token), "update")), "sounds.update"));
        }

        stack.Children.Add(Tagged(DangerRow("Remove pack", "Restores defaults first if this pack is selected",
            Icons.Close,
            () => Navigate(() => RenderRemove(pack))), "sounds.remove"));
        SetContent(stack);
    }

    private void RenderRemove(SoundPack pack)
    {
        var stack = NewStack("Remove " + pack.Name + "?");
        stack.Children.Add(DangerRow("Remove", "Deletes this pack from WSGM's library", Icons.Close, () =>
        {
            if (_service is { } service)
            {
                Run(token => service.DeleteAsync(pack.Id, token), "remove");
            }

            Back();
        }));
        stack.Children.Add(Row("Cancel", "Keep this pack", Icons.ArrowLeft, () => Back()));
        SetContent(stack);
    }

    private void RenderBrowse()
    {
        if (_service is not { } service)
        {
            return;
        }

        var state = service.ReadState();
        var stack = NewStack("Browse sound packs");
        AddStatus(stack, state.Busy, state.Error, null);
        stack.Children.Add(Tagged(Row("Search", _search, Icons.ListLines, () => EditText("Search sound packs", _search,
            64, text =>
            {
                _search = text;
                Run(token => service.BrowseAsync(1, text, token), "search");
            })), "sounds.search"));
        foreach (var listing in state.Listings)
        {
            var item = listing;
            stack.Children.Add(Tagged(Row(item.DisplayName.Length > 0 ? item.DisplayName : item.Name,
                    item.SpecifiedAuthor + " · " + item.Version, Icons.ArrowDown,
                    () => Navigate(() => RenderListing(item))),
                "sounds.listing." + item.Id));
        }

        if (state.Page > 1)
        {
            stack.Children.Add(Row("Previous page", "", Icons.ArrowLeft,
                () => Run(token => service.BrowseAsync(state.Page - 1, _search, token), "browse")));
        }

        if (state.Page * 24 < state.Total)
        {
            stack.Children.Add(Row("Next page", "", Icons.ArrowDown,
                () => Run(token => service.BrowseAsync(state.Page + 1, _search, token), "browse")));
        }

        SetContent(stack);
    }

    private void RenderListing(ThemeStoreSummary item)
    {
        var stack = NewStack(item.DisplayName.Length > 0 ? item.DisplayName : item.Name);
        stack.Children.Add(Caption(item.SpecifiedAuthor + " · " + item.Version));
        stack.Children.Add(PrimaryRow("Install", "Download this Audio Loader pack from DeckThemes", Icons.ArrowDown,
            () =>
            {
                if (_service is { } service)
                {
                    Run(token => service.InstallAsync(item.Id, token), "install");
                }
            }));
        if (_service is { } owner)
        {
            var state = owner.ReadState();
            AddStatus(stack, state.Busy, state.Error, null);
            if (state.Packs.Any(pack => pack.StoreId == item.Id))
            {
                stack.Children.Add(Caption("Installed. Choose it from Installed packs."));
            }
        }

        SetContent(stack);
    }
}
