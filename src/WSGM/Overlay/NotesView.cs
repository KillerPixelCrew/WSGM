using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The overlay's Notes: the running game's notebook, its pages, and a page editor with text blocks, finger
///     and pen ink and game screenshots, over the session's <see cref="NotesService" />.
/// </summary>
/// <remarks>
///     <para>
///         Every edit is saved as it is made, against the revision the page showed, so there is no unsaved page
///         to lose when the overlay closes. The one thing typed but not yet saved, an open text editor, is kept
///         in the service and comes back when the overlay reopens.
///     </para>
///     <para>
///         The page has three explicit modes. Navigate selects, moves and resizes; Text taps to write; Ink owns
///         every contact on the page, so drawing never scrolls the sheet. A controller reaches every element and
///         action through rows below the page; touch and pen work on the page itself.
///     </para>
///     <para>
///         Images are decoded only while a page that shows them is open, and released when it closes.
///     </para>
/// </remarks>
public sealed class NotesView : ServiceSubView
{
    private const double Nudge = 40;

    private static readonly (string Color, string Name)[] Palette =
    [
        ("#FFFFFF", "White"), ("#FFD93D", "Yellow"), ("#FF5A5A", "Red"), ("#5BD46B", "Green"), ("#4DA3FF", "Blue"),
        ("#111111", "Black")
    ];

    private static readonly (double Width, string Name)[] Widths = [(3, "Fine"), (6, "Medium"), (12, "Bold")];

    private readonly Dictionary<string, Bitmap?> _images = new(StringComparer.Ordinal);
    private readonly Stack<InkStep> _redo = new();
    private readonly Stack<InkStep> _undo = new();
    private NotesCanvas? _canvas;
    private StackPanel? _details;
    private StackPanel? _tools;
    private NotesCurrentGame _game = NotesCurrentGame.NoGame;
    private NotePage? _page;
    private StackPanel? _pageRoot;
    private string? _pendingConflict;
    private NotesService? _service;
    private string _inkColor = "#FFFFFF";
    private NotesInkTool _inkTool = NotesInkTool.Pen;
    private double _inkWidth = 6;
    private TopLevel? _window;
    private int _columns = 2;

    /// <summary>Keeps the notebook and editor fitted to the current Overlay window.</summary>
    public NotesView()
    {
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this);
            if (_window is not null)
            {
                _window.SizeChanged += WindowResized;
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_window is not null)
            {
                _window.SizeChanged -= WindowResized;
            }

            _window = null;
        };
        SizeChanged += (_, args) =>
        {
            var columns = args.NewSize.Width < 500 ? 1 : 2;
            if (_columns != columns)
            {
                _columns = columns;
                CurrentLevel?.Invoke();
            }
        };
    }

    private void WindowResized(object? sender, SizeChangedEventArgs args)
    {
        if (_canvas is not null)
        {
            SizeCanvas();
        }
    }

    /// <summary>Starts an Add Screenshot from this overlay for a page; set by the overlay controller.</summary>
    internal Func<string, int, SteamUiCommandResult>? StartCapture { get; set; }

    /// <inheritdoc />
    protected override string LogScope => "Notes";

    /// <summary>Follows the session's Notes owner, or none with null.</summary>
    /// <param name="service">The owner, or null when the overlay closes or the session has none.</param>
    internal void Attach(NotesService? service)
    {
        _service = service;
        AttachSource(IsEffectivelyVisible ? service : null);
    }

    /// <summary>Opens the page the overlay last showed, or the running game's notebook.</summary>
    internal void OpenLastLocation()
    {
        Open();
    }

    /// <inheritdoc />
    internal override void Leave()
    {
        SaveLocation();
        ReleasePage();
        _pageRoot = null;
        _details = null;
        _tools = null;
        base.Leave();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            AttachSource(IsVisible ? _service : null);
        }
    }

    /// <inheritdoc />
    private protected override void RenderHome()
    {
        if (_service is not { } service)
        {
            RenderMessage("Notes", "Notes are unavailable in this session.");
            return;
        }

        if (!service.Loaded)
        {
            RenderMessage("Notes", "Opening your notes…");
            var generation = NavigationGeneration;
            _ = RunSafelyAsync(LoadAsync(), "load");
            return;

            async Task LoadAsync()
            {
                await Task.Run(service.EnsureLoaded);
                if (generation == NavigationGeneration)
                {
                    RenderHome();
                }
            }
        }

        if (service.LoadError is { } error)
        {
            RenderMessage("Notes", error);
            return;
        }

        _game = service.Games.ResolveCurrentGame();
        var location = service.OverlayLocation;
        var scope = location.Scope ?? _game.Scope ?? NoteScope.Global;
        if (location.Scope is not null && _game is { State: NotesGameState.Game, Scope: { } current }
                                       && location.Scope != current && location.PageId is null)
        {
            // A different game is running than when Notes was last open: its notebook comes first.
            scope = current;
        }

        service.OverlayLocation = location with { Scope = scope };
        if (location.PageId is { } pageId && service.ReadPage(pageId) is { } page && page.Scope == scope)
        {
            // Unsaved text, if any, is offered on the page itself rather than reopened unasked.
            RenderNotebook(scope);
            Navigate(() => RenderPage(pageId));
            return;
        }

        RenderNotebook(scope);
    }

    private void RenderNotebook(string scope)
    {
        var service = _service!;
        ReleasePage();
        _pageRoot = null;
        service.OverlayLocation = service.OverlayLocation with { Scope = scope, PageId = null };
        var title = service.NotebookTitle(scope);
        var body = NewStack("Notes");
        body.Children.Add(ChoiceRow("Notebook", service.ReadNotebooks(_game)
            .Select(book => (book.Scope, book.Title + (book.Scope == _game.Scope ? " · Running now" : ""))).ToArray(), scope,
            selected =>
            {
                NavigationStack.Clear();
                Replace(() => RenderNotebook(selected));
            }));
        body.Children.Add(Tagged(Caption(ScopeCaption(scope)), "notes.scope"));
        AddCaptureOutcome(body, service);
        body.Children.Add(CommandStrip(Command("notes.create", "New page", () => Create(scope), true)));
        var pages = service.ReadPages(scope);
        var grid = new UniformGrid { Columns = _columns, Tag = "notes.library:" + _columns };
        foreach (var summary in pages)
        {
            var card = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 12, 12), Tag = "notes.card:" + summary.Id };
            var preview = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
            var kind = summary.ReadOnly ? "READ ONLY" : summary.Pinned ? "PINNED PAGE" : "NOTE PAGE";
            preview.Children.Add(SectionLabel(kind));
            preview.Children.Add(new TextBlock
            {
                Text = summary.Preview.Length > 0 ? summary.Preview : summary.Images > 0 ? "Screenshots and annotations"
                    : summary.Strokes > 0 ? "Handwritten notes" : "A fresh page for your next idea",
                FontSize = 17, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis
            });
            var cover = new Border { Child = preview, Height = 142, CornerRadius = new CornerRadius(8, 8, 0, 0), ClipToBounds = true };
            cover.Bind(Border.BackgroundProperty, cover.GetResourceObservable("DeckGroupBrush"));
            card.Children.Add(cover);
            var detail = string.Join(" · ", new[] { Count(summary.Texts, "text block"), Count(summary.Images, "image"),
                summary.Strokes > 0 ? "Ink" : null }.Where(part => part is not null));
            card.Children.Add(Tagged(Row(summary.Title, detail.Length > 0 ? detail : "Empty page", null,
                () => Navigate(() => RenderPage(summary.Id))), "notes.page:" + summary.Id));
            card.Children.Add(Caption("Edited " + summary.Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
            grid.Children.Add(card);
        }

        if (pages.Count == 0)
        {
            var empty = new StackPanel { Spacing = 14, Margin = new Thickness(24, 36) };
            empty.Children.Add(new TextBlock { Text = "Your notebook starts here", FontSize = 24, FontWeight = FontWeight.SemiBold });
            empty.Children.Add(Caption($"Create a page in {title} for text, drawings and game screenshots. Changes stay on this PC."));
            body.Children.Add(empty);
        }
        else
        {
            body.Children.Add(grid);
        }
        SetContent(body);
    }

    private string ScopeCaption(string scope)
    {
        if (scope == NoteScope.Global)
        {
            return _game.State switch
            {
                NotesGameState.Unknown => $"Global notes. {_game.Detail} Nothing is saved to a game's notebook until it is known.",
                NotesGameState.Game => $"Global notes. {_game.Title} has its own notebook under Switch notebook.",
                _ => "Global notes, not tied to any game."
            };
        }

        return scope == _game.Scope ? $"Notes for {_game.Title}, the game running now." : "Notes for this game.";
    }

    private void Create(string scope)
    {
        if (_service is not { } service)
        {
            return;
        }

        _ = RunSafelyAsync(CreateAsync(), "create");
        return;

        async Task CreateAsync()
        {
            var generation = NavigationGeneration;
            var result = await Task.Run(() => service.CreatePage(scope));
            if (generation != NavigationGeneration)
            {
                return;
            }

            if (result is { Succeeded: true, Page: { } page })
            {
                Navigate(() => RenderPage(page.Id));
            }
            else
            {
                Toast(result.Error ?? "The page could not be created.");
            }
        }
    }

    // ---- The page ----

    private void RenderPage(string pageId)
    {
        if (_service is not { } service)
        {
            return;
        }

        var page = service.ReadPage(pageId);
        if (page is null)
        {
            ReleasePage();
            RenderMessage("Page removed", "This page was deleted, here or in Steam's overlay.");
            return;
        }

        if (_page?.Id != page.Id)
        {
            ReleasePage();
        }

        _page = page;
        service.OverlayLocation = service.OverlayLocation with { Scope = page.Scope, PageId = page.Id };
        if (_pageRoot is not null && _canvas is not null)
        {
            // A publication while the page is open: refresh in place so a drag, the mode and focus survive.
            _canvas.Page = page;
            RenderDetails();
            if (!ReferenceEquals(Content, _pageRoot))
            {
                SetContent(_pageRoot);
            }
            return;
        }

        var location = service.OverlayLocation;
        var body = NewStack(page.Title);
        _canvas = new NotesCanvas
        {
            Page = page, Height = 380, Mode = ParseMode(location.Mode), InkTool = _inkTool, InkColor = _inkColor,
            InkWidth = _inkWidth, Zoom = location.Zoom, ImageSource = ImageFor
        };
        _canvas.Scroll = new Point(0, location.ScrollY);
        _canvas.StrokeCompleted += OnStrokeCompleted;
        _canvas.StrokesErased += OnStrokesErased;
        _canvas.ElementPlaced += OnElementPlaced;
        _canvas.ElementSelected += _ => RenderDetails();
        _canvas.TextRequested += OnTextRequested;
        _canvas.Scrolled += _ => SaveLocation();
        // The mode and tool bars sit above the page so they stay in view at the 640-DIP floor; the
        // element list and page actions follow it.
        _tools = new StackPanel { Spacing = 6, Tag = "notes.tools" };
        _details = new StackPanel { Spacing = 10, Tag = "notes.details" };
        body.Children.Add(_tools);
        body.Children.Add(new Border
        {
            Child = _canvas, CornerRadius = new CornerRadius(8), ClipToBounds = true, Tag = "notes.canvas"
        });
        body.Children.Add(_details);
        _pageRoot = body;
        RenderDetails();
        SizeCanvas();
        SetContent(body);
        LoadImages(page);
    }

    private void RenderDetails()
    {
        if (_details is null || _tools is null || _canvas is null || _page is not { } page || _service is not { } service)
        {
            return;
        }

        var focusedTag = (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control)?.Tag;
        var readOnly = service.IsReadOnly(page.Id);
        _details.Children.Clear();
        _tools.Children.Clear();
        if (_pageRoot?.Children.FirstOrDefault() is TextBlock heading)
        {
            heading.Text = page.Title;
        }
        if (_pendingConflict is { } conflict)
        {
            _tools.Children.Add(Tagged(Caption(conflict), "notes.conflict"));
        }

        AddCaptureOutcome(_tools, service);
        if (service.OverlayTextDraft is { } draft && draft.PageId == page.Id && !readOnly && Unsaved(page, draft))
        {
            _tools.Children.Add(Tagged(PrimaryRow("Resume unsaved text", FirstLine(draft.Text), Icons.Keyboard,
                () => OpenEditor(draft)), "notes.draft.resume"));
            _tools.Children.Add(Tagged(Row("Discard unsaved text", "The page keeps what was saved", Icons.Close,
                () =>
                {
                    service.OverlayTextDraft = null;
                    RenderDetails();
                }), "notes.draft.discard"));
        }

        if (readOnly)
        {
            _tools.Children.Add(Caption("This page was saved by a newer WSGM. It is shown, but not changed here."));
        }

        // Three rows at most: the modes with the two additions, then navigation or the ink tools, then
        // the ink widths and colors.
        var mode = _canvas.Mode;
        _tools.Children.Add(CommandStrip([
            Command("notes.mode.navigate", "Navigate", () => SetMode(NotesCanvasMode.Navigate),
                mode == NotesCanvasMode.Navigate),
            Command("notes.mode.text", "Text", readOnly ? null : () => SetMode(NotesCanvasMode.Text),
                mode == NotesCanvasMode.Text),
            Command("notes.mode.ink", "Ink", readOnly ? null : () => SetMode(NotesCanvasMode.Ink),
                mode == NotesCanvasMode.Ink),
            .. readOnly
                ? Array.Empty<Control>()
                :
                [
                    Command("notes.add.text", "Add text", () => OnTextRequested(null, _canvas.VisibleCenter())),
                    Command("notes.add.screenshot", "Screenshot",
                        StartCapture is not null && !service.Capture.Pending ? () => Navigate(RenderCapture) : null)
                ],
            Command("notes.options", "Page…", () => Navigate(RenderPageOptions))
        ]));
        if (mode == NotesCanvasMode.Ink && !readOnly)
        {
            _tools.Children.Add(CommandStrip(
                Command("notes.tool.pen", "Pen", () => SetTool(NotesInkTool.Pen), _inkTool == NotesInkTool.Pen),
                Command("notes.tool.highlighter", "Highlighter", () => SetTool(NotesInkTool.Highlighter),
                    _inkTool == NotesInkTool.Highlighter),
                Command("notes.tool.eraser", "Eraser", () => SetTool(NotesInkTool.Eraser),
                    _inkTool == NotesInkTool.Eraser),
                Command("notes.undo", "Undo", _undo.Count > 0 ? Undo : null),
                Command("notes.redo", "Redo", _redo.Count > 0 ? Redo : null),
                Command("notes.clear", "Clear ink…", page.Strokes.Count > 0 ? ConfirmClearInk : null)));
            var widthChoice = ChoiceRow("Width", Widths.Select(width => (width.Width, width.Name)).ToArray(), _inkWidth, SetWidth);
            widthChoice.Width = 240;
            widthChoice.Margin = new Thickness(0, 0, 8, 8);
            _tools.Children.Add(CommandStrip([
                widthChoice,
                .. Palette.Select(color => Swatch(color.Color, color.Name))
            ]));
        }
        else
        {
            _tools.Children.Add(CommandStrip(
                Command("notes.zoom.out", "Zoom −", _canvas.Zoom > 1 ? () => SetZoom(_canvas.Zoom - 0.5) : null),
                Command("notes.zoom.in", "Zoom +", _canvas.Zoom < 4 ? () => SetZoom(_canvas.Zoom + 0.5) : null),
                Command("notes.scroll.up", "Page up", () => _canvas.ScrollBy(new Vector(0, -360))),
                Command("notes.scroll.down", "Page down", () => _canvas.ScrollBy(new Vector(0, 360)))));
        }

        SizeCanvas();

        if (focusedTag is not null && _tools.GetLogicalDescendants().Concat(_details.GetLogicalDescendants())
                .OfType<Control>().FirstOrDefault(control => Equals(control.Tag, focusedTag) && control.Focusable)
            is { } refocus)
        {
            refocus.Focus(NavigationMethod.Directional);
        }
    }

    private void RenderContents()
    {
        if (_page is null || _service?.ReadPage(_page.Id) is not { } page)
        {
            Back();
            return;
        }

        _page = page;
        var body = NewStack("Page contents");
        body.Children.Add(Caption("Select a text block or screenshot to bring it into view on the page."));
        foreach (var element in page.Elements)
        {
            body.Children.Add(Tagged(Row(element.Kind == NoteElement.ImageKind ? "Screenshot" : FirstLine(element.Text),
                element.Kind == NoteElement.ImageKind ? $"{element.PixelWidth} × {element.PixelHeight}" : "Text block",
                element.Kind == NoteElement.ImageKind ? Icons.Monitor : Icons.ListLines, () =>
                {
                    Select(element.Id);
                    PopIfAny();
                    Back();
                }), "notes.element:" + element.Id));
        }

        if (page.Elements.Count == 0)
        {
            body.Children.Add(Caption("This page has no text or images yet. Return to add text, ink or a screenshot."));
        }

        SetContent(body);
    }

    private void RenderPageOptions()
    {
        if (_page is null || _service is not { } service || service.ReadPage(_page.Id) is not { } page)
        {
            Back();
            return;
        }

        _page = page;
        var body = NewStack("Page options");
        body.Children.Add(Caption($"{service.NotebookTitle(page.Scope)} · Saved on this PC · {page.Modified.ToLocalTime():g}"));
        body.Children.Add(Tagged(Row("Page contents", $"{page.Elements.Count} items", Icons.ListLines,
            () => Navigate(RenderContents)), "notes.contents"));
        if (_canvas?.SelectedElement is { } selectedId && page.Elements.Find(item => item.Id == selectedId) is { } selected
            && !service.IsReadOnly(page.Id))
        {
            if (selected.Kind == NoteElement.TextKind)
            {
                body.Children.Add(Tagged(Row("Edit selected text", FirstLine(selected.Text), Icons.Keyboard,
                    () => OnTextRequested(selected.Id, default)), "notes.selection.edit"));
            }

            body.Children.Add(Tagged(Row("Arrange selection", "Move, resize, bring to front or remove", Icons.Grid4,
                () => Navigate(RenderSelection)), "notes.selection.arrange"));
        }
        if (!service.IsReadOnly(page.Id))
        {
            body.Children.Add(Tagged(Row("Rename page", page.Title, Icons.Keyboard, EditTitle), "notes.title"));
            body.Children.Add(Tagged(Row(page.Pinned ? "Unpin page" : "Pin page", "Pinned pages stay at the top", Icons.Pin,
                () => Run(_ => Task.FromResult(service.SetPinned(page.Id, !page.Pinned).ToCommandResult()))), "notes.pin"));
            body.Children.Add(CommandStrip(
                Command("notes.move.up", "Move page up",
                    () => Run(_ => Task.FromResult(service.MovePage(page.Id, -1).ToCommandResult()))),
                Command("notes.move.down", "Move page down",
                    () => Run(_ => Task.FromResult(service.MovePage(page.Id, 1).ToCommandResult())))));
            body.Children.Add(Tagged(DangerRow("Delete page", "Removes its text, drawings and screenshots", Icons.Close,
                () => ConfirmCommand("Delete page", $"Delete “{page.Title}”? This cannot be undone.",
                    _ => Task.FromResult(DeleteAndReturn(page)))), "notes.delete"));
        }
        else
        {
            body.Children.Add(Caption("This page was saved by a newer WSGM and is read-only."));
        }

        SetContent(body);
    }

    private void RenderSelection()
    {
        if (_page is null || _service?.ReadPage(_page.Id) is not { } page)
        {
            Back();
            return;
        }

        _page = page;
        var body = NewStack("Arrange selection");
        AddSelection(body, page, _service.IsReadOnly(page.Id));
        SetContent(body);
    }

    private void SizeCanvas()
    {
        if (_canvas is not null)
        {
            _canvas.Height = Math.Clamp((_window?.Bounds.Height ?? 800) - (_canvas.Mode == NotesCanvasMode.Ink ? 470 : 430), 180, 430);
        }
    }

    private void AddSelection(StackPanel target, NotePage page, bool readOnly)
    {
        if (_canvas?.SelectedElement is not { } id || page.Elements.Find(item => item.Id == id) is not { } element
                                                   || readOnly)
        {
            return;
        }

        var revision = page.Revision;
        target.Children.Add(SectionLabel(element.Kind == NoteElement.ImageKind ? "Selected screenshot" : "Selected text"));
        if (element.Kind == NoteElement.TextKind)
        {
            target.Children.Add(Tagged(PrimaryRow("Edit text", FirstLine(element.Text), Icons.Keyboard,
                () => OnTextRequested(element.Id, default)), "notes.selection.edit"));
        }

        target.Children.Add(CommandStrip(
            Command("notes.selection.left", "◀", () => Place(element, -Nudge, 0, 1)),
            Command("notes.selection.up", "▲", () => Place(element, 0, -Nudge, 1)),
            Command("notes.selection.down", "▼", () => Place(element, 0, Nudge, 1)),
            Command("notes.selection.right", "▶", () => Place(element, Nudge, 0, 1)),
            Command("notes.selection.smaller", "Smaller", () => Place(element, 0, 0, 0.85)),
            Command("notes.selection.bigger", "Bigger", () => Place(element, 0, 0, 1.15)),
            Command("notes.selection.front", "To front",
                () => Edit(() => _service!.RaiseElement(page.Id, revision, element.Id)))));
        target.Children.Add(Tagged(DangerRow(element.Kind == NoteElement.ImageKind ? "Delete screenshot" : "Delete text",
            "The rest of the page stays", Icons.Close,
            () => ConfirmCommand("Delete selection", "Remove this item? The rest of the page stays.",
                _ => Task.FromResult(_service!.RemoveElement(page.Id, revision, element.Id).ToCommandResult()))), "notes.selection.delete"));
    }

    private SteamUiCommandResult DeleteAndReturn(NotePage page)
    {
        var result = _service!.DeletePage(page.Id, page.Revision);
        if (result.Succeeded)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ReleasePage();
                NavigationStack.Clear();
                CurrentLevel = null;
                Navigate(() => RenderNotebook(page.Scope));
            });
        }

        return result.ToCommandResult();
    }

    // ---- Editing ----

    private void EditTitle()
    {
        if (_page is not { } page)
        {
            return;
        }

        EditText("Page title", page.Title, 0, title => Edit(() => _service!.RenamePage(page.Id, page.Revision, title)));
    }

    private void OnTextRequested(string? elementId, Point at)
    {
        if (_page is not { } page || _service is not { } service || service.IsReadOnly(page.Id))
        {
            return;
        }

        if (elementId is not null && service.OverlayTextDraft is { } kept && kept.PageId == page.Id
            && kept.ElementId == elementId)
        {
            // The same block again: continue the unsaved text rather than start over from the saved one.
            OpenEditor(kept);
            return;
        }

        var text = elementId is null ? "" : page.Elements.Find(item => item.Id == elementId)?.Text ?? "";
        OpenEditor(new NotesTextDraft(page.Id, elementId, page.Revision, text, at.X, at.Y));
    }

    // The overlay's own full-width keyboard surface, multiline: Enter starts a new line and Accept saves.
    // Every edit is kept as the draft, so a cancelled keyboard or a closed overlay loses nothing; the page
    // then offers to resume or discard it.
    private void OpenEditor(NotesTextDraft draft)
    {
        if (_service is not { } service)
        {
            return;
        }

        service.OverlayTextDraft = draft;
        var prompt = draft.ElementId is null ? "New text" : "Edit text";
        if (OverlayWindow.RequestTextFor(this, prompt, draft.Text, 0, SaveText, true, text =>
            {
                if (service.OverlayTextDraft is { } current && current.PageId == draft.PageId)
                {
                    service.OverlayTextDraft = current with { Text = text };
                }
            }))
        {
            RenderDetails();
            return;
        }

        Log.Warn("Notes: cannot edit text, no keyboard surface is available.");
        Toast("Text entry needs the overlay keyboard, which is not available.");
    }

    private void SaveText(string text)
    {
        if (_service is not { } service || service.OverlayTextDraft is not { } draft)
        {
            return;
        }

        _ = RunSafelyAsync(SaveAsync(), "save text");
        return;

        async Task SaveAsync()
        {
            var result = await Task.Run(() => draft.ElementId is { } id
                ? service.SetText(draft.PageId, draft.BaseRevision, id, text)
                : service.AddText(draft.PageId, draft.BaseRevision, draft.X, draft.Y, text));
            if (result.Succeeded)
            {
                service.OverlayTextDraft = null;
                _pendingConflict = null;
                if (result.ElementId is { } created && _canvas is not null)
                {
                    _canvas.SelectedElement = created;
                }

                RenderDetails();
                return;
            }

            if (result.Status == NoteEditStatus.Conflict && result.Page is { } newest)
            {
                var kept = draft with { Text = text };
                service.OverlayTextDraft = kept;
                Navigate(() => RenderTextConflict(kept, newest));
                return;
            }

            service.OverlayTextDraft = draft with { Text = text };
            Toast(result.Error ?? "The text could not be saved.");
        }
    }

    private void RenderTextConflict(NotesTextDraft draft, NotePage newest)
    {
        var body = NewStack("Changed in Steam's overlay");
        body.Children.Add(Caption("This page changed elsewhere after you started typing. Nothing was overwritten. "
                                  + "Choose what to do with your text."));
        body.Children.Add(Caption("Your text: " + FirstLine(draft.Text)));
        var exists = draft.ElementId is null || newest.Elements.Any(item => item.Id == draft.ElementId);
        if (exists)
        {
            body.Children.Add(Tagged(PrimaryRow(draft.ElementId is null ? "Add my text to the newest version" : "Replace with my text",
                "Applies your text to the page as it is now", Icons.CopyDoc,
                () => Reapply(draft with { BaseRevision = newest.Revision })), "notes.conflict.apply"));
        }

        body.Children.Add(Tagged(Row("Add my text as a new block", "Keeps both versions", Icons.FolderPlus,
            () => Reapply(draft with { ElementId = null, BaseRevision = newest.Revision })), "notes.conflict.copy"));
        body.Children.Add(Tagged(DangerRow("Discard my text", "Shows the newest version", Icons.Close, DiscardText),
            "notes.conflict.discard"));
        SetContent(body);
    }

    private void Reapply(NotesTextDraft draft)
    {
        _service!.OverlayTextDraft = draft;
        Back();
        SaveText(draft.Text);
    }

    private void DiscardText()
    {
        if (_service is { } service)
        {
            service.OverlayTextDraft = null;
        }

        Back();
    }

    private void OnStrokeCompleted(NoteStroke stroke)
    {
        if (_page is not { } page)
        {
            return;
        }

        RunEdit(() => _service!.AddStrokes(page.Id, page.Revision, [stroke]),
            () => PushUndo(new InkStep([stroke], null, true)),
            newest => _service!.AddStrokes(page.Id, newest.Revision, [stroke]));
    }

    private void OnStrokesErased(IReadOnlyCollection<string> ids)
    {
        if (_page is not { } page)
        {
            return;
        }

        var removed = page.Strokes.Select((stroke, index) => (stroke, index)).Where(item => ids.Contains(item.stroke.Id))
            .ToArray();
        if (removed.Length == 0)
        {
            return;
        }

        RunEdit(() => _service!.RemoveStrokes(page.Id, page.Revision, ids),
            () => PushUndo(new InkStep([.. removed.Select(item => item.stroke)], [.. removed.Select(item => item.index)], false)),
            newest => _service!.RemoveStrokes(page.Id, newest.Revision, ids));
    }

    private void ConfirmClearInk()
    {
        if (_page is not { } page)
        {
            return;
        }

        var strokes = page.Strokes.ToArray();
        ConfirmCommand("Clear ink", "Remove every drawing on this page? Text and screenshots stay. Undo can bring it back.",
            _ =>
            {
                var result = _service!.RemoveStrokes(page.Id, page.Revision, null);
                if (result.Succeeded)
                {
                    Dispatcher.UIThread.Post(() => PushUndo(new InkStep(strokes,
                        [.. Enumerable.Range(0, strokes.Length)], false)));
                }

                return Task.FromResult(result.ToCommandResult());
            });
    }

    private void Undo()
    {
        if (_undo.Count == 0 || _page is not { } page)
        {
            return;
        }

        var step = _undo.Pop();
        Apply(step, true, page, () => _redo.Push(step));
    }

    private void Redo()
    {
        if (_redo.Count == 0 || _page is not { } page)
        {
            return;
        }

        var step = _redo.Pop();
        Apply(step, false, page, () => _undo.Push(step));
    }

    private void Apply(InkStep step, bool undo, NotePage page, Action done)
    {
        // Undoing an addition removes it; undoing an erase puts the strokes back where they were.
        var remove = step.Added == undo;
        var ids = step.Strokes.Select(stroke => stroke.Id).ToHashSet(StringComparer.Ordinal);
        RunEdit(() => remove
                ? _service!.RemoveStrokes(page.Id, page.Revision, ids)
                : _service!.AddStrokes(page.Id, page.Revision, step.Strokes, step.Indices),
            done, null);
    }

    private void PushUndo(InkStep step)
    {
        _undo.Push(step);
        _redo.Clear();
        RenderDetails();
    }

    private void OnElementPlaced(string id, Rect rect)
    {
        if (_page is not { } page)
        {
            return;
        }

        Edit(() => _service!.PlaceElement(page.Id, page.Revision, id, rect.X, rect.Y, rect.Width, rect.Height));
    }

    private void Place(NoteElement element, double dx, double dy, double factor)
    {
        if (_page is not { } page)
        {
            return;
        }

        var width = element.Width * factor;
        var height = element.Height * factor;
        Edit(() => _service!.PlaceElement(page.Id, page.Revision, element.Id, element.X + dx, element.Y + dy,
            width, height));
    }

    private void Edit(Func<NoteEditResult> edit)
    {
        RunEdit(edit, null, null);
    }

    private void RunEdit(Func<NoteEditResult> edit, Action? applied, Func<NotePage, NoteEditResult>? reapply)
    {
        _ = RunSafelyAsync(RunAsync(), "edit");
        return;

        async Task RunAsync()
        {
            var result = await Task.Run(edit);
            if (result.Succeeded)
            {
                _pendingConflict = null;
                applied?.Invoke();
                if (result.Page is { } saved && _canvas is not null && saved.Id == _page?.Id)
                {
                    _page = saved;
                    _canvas.Page = saved;
                    RenderDetails();
                }

                return;
            }

            if (result.Status == NoteEditStatus.Conflict && result.Page is { } newest)
            {
                // The other overlay saved first. The page now shows its version; the undo history no longer
                // describes it.
                _undo.Clear();
                _redo.Clear();
                _page = newest;
                if (_canvas is not null)
                {
                    _canvas.Page = newest;
                }

                if (reapply is not null)
                {
                    Navigate(() => RenderInkConflict(newest, reapply, applied));
                    return;
                }

                _pendingConflict = result.Error;
                RenderDetails();
                return;
            }

            Toast(result.Error ?? "The change could not be saved.");
        }
    }

    private void RenderInkConflict(NotePage newest, Func<NotePage, NoteEditResult> reapply, Action? applied)
    {
        var body = NewStack("Changed in Steam's overlay");
        body.Children.Add(Caption("This page changed elsewhere while you drew. Nothing was overwritten."));
        body.Children.Add(Tagged(PrimaryRow("Apply my ink to the newest version", "Keeps both changes", Icons.CopyDoc,
            () =>
            {
                Back();
                RunEdit(() => reapply(_service!.ReadPage(newest.Id) ?? newest), applied, null);
            }), "notes.conflict.apply"));
        body.Children.Add(Tagged(DangerRow("Discard my ink", "Shows the newest version", Icons.Close, () => Back()),
            "notes.conflict.discard"));
        SetContent(body);
    }

    // ---- Add screenshot ----

    private void RenderCapture()
    {
        if (_service is not { } service || _page is not { } page)
        {
            Back();
            return;
        }

        var seconds = service.CountdownSeconds;
        var body = NewStack("Add screenshot");
        body.Children.Add(Caption("Notes closes and returns you to the app or desktop. Use the countdown to prepare "
                                  + "the picture. The app's window is captured, or this display when opened from the "
                                  + "desktop, then this page opens again with the screenshot added. Open the overlay "
                                  + "during the countdown to cancel."));
        body.Children.Add(ChoiceRow("Countdown",
            Enumerable.Range(NoteSettings.MinimumCountdown,
                NoteSettings.MaximumCountdown - NoteSettings.MinimumCountdown + 1).Select(value => (value, $"{value} seconds"))
                .ToArray(), seconds, value => service.SetCountdown(value)));
        body.Children.Add(Tagged(PrimaryRow("Start countdown", "Returns to the app or desktop now", Icons.Play, () =>
        {
            var result = StartCapture?.Invoke(page.Id, service.CountdownSeconds)
                         ?? new SteamUiCommandResult(false, "Screenshots are not available here.");
            if (!result.Succeeded)
            {
                Toast(result.Error ?? "The screenshot could not start.");
            }
        }), "notes.capture.start"));
        body.Children.Add(Tagged(Row("Cancel", "Stay on this page", Icons.ArrowLeft, () => Back()), "notes.capture.cancel"));
        SetContent(body);
    }

    private void AddCaptureOutcome(StackPanel stack, NotesService service)
    {
        var status = service.Capture.Status;
        if (status.Phase == NotesCapturePhase.Countdown)
        {
            stack.Children.Add(Caption($"Screenshot in {status.Remaining} s…"));
        }
        else if (status is { Phase: NotesCapturePhase.Idle, Outcome: { } outcome })
        {
            stack.Children.Add(Tagged(Row(outcome, "Dismiss", status.OutcomeIsError ? Icons.Info : Icons.Monitor,
                service.Capture.ClearOutcome), "notes.capture.outcome"));
            if (!status.OutcomeIsError && status.InsertedElementId is { } inserted && _canvas is not null
                && _canvas.SelectedElement is null)
            {
                _canvas.SelectedElement = inserted;
            }
        }
    }

    // ---- Presentation helpers ----

    private void SetMode(NotesCanvasMode mode)
    {
        if (_canvas is null)
        {
            return;
        }

        _canvas.Mode = mode;
        SaveLocation();
        RenderDetails();
    }

    private void SetTool(NotesInkTool tool)
    {
        _inkTool = tool;
        if (_canvas is not null)
        {
            _canvas.InkTool = tool;
        }

        RenderDetails();
    }

    private void SetWidth(double width)
    {
        _inkWidth = width;
        if (_canvas is not null)
        {
            _canvas.InkWidth = width;
        }

        RenderDetails();
    }

    private void SetColor(string color)
    {
        _inkColor = color;
        if (_canvas is not null)
        {
            _canvas.InkColor = color;
            if (_inkTool == NotesInkTool.Eraser)
            {
                SetTool(NotesInkTool.Pen);
                return;
            }
        }

        RenderDetails();
    }

    private void SetZoom(double zoom)
    {
        if (_canvas is null)
        {
            return;
        }

        _canvas.Zoom = zoom;
        SaveLocation();
        RenderDetails();
    }

    private void Select(string id)
    {
        if (_canvas is null)
        {
            return;
        }

        _canvas.SelectedElement = _canvas.SelectedElement == id ? null : id;
        if (_page?.Elements.Find(item => item.Id == id) is { } element && _canvas.SelectedElement is not null)
        {
            // Bring the selection into view for a controller user who chose it from the list.
            _canvas.Scroll = new Point(_canvas.Scroll.X, Math.Max(0, element.Y - 40));
        }

        RenderDetails();
    }

    private void SaveLocation()
    {
        if (_service is not { } service || _canvas is null)
        {
            return;
        }

        service.OverlayLocation = service.OverlayLocation with
        {
            Mode = _canvas.Mode.ToString().ToLowerInvariant(), Zoom = _canvas.Zoom, ScrollY = _canvas.Scroll.Y
        };
    }

    private static NotesCanvasMode ParseMode(string mode)
    {
        return mode switch
        {
            "ink" => NotesCanvasMode.Ink,
            "text" => NotesCanvasMode.Text,
            _ => NotesCanvasMode.Navigate
        };
    }

    private void LoadImages(NotePage page)
    {
        if (_service is not { } service)
        {
            return;
        }

        foreach (var asset in NoteDocumentRules.AssetsOf(page).Where(asset => !_images.ContainsKey(asset)).ToArray())
        {
            _images[asset] = null;
            var path = service.AssetPath(asset);
            _ = RunSafelyAsync(LoadAsync(), "image");

            async Task LoadAsync()
            {
                var bitmap = await Task.Run(() =>
                {
                    if (path is null || !File.Exists(path))
                    {
                        return null;
                    }

                    using var stream = File.OpenRead(path);
                    // Decoded at the most the page can show, not the screenshot's full resolution.
                    return Bitmap.DecodeToWidth(stream, 1600);
                });
                if (_page?.Id == page.Id && _images.ContainsKey(asset))
                {
                    _images[asset] = bitmap;
                    _canvas?.Refresh();
                }
                else
                {
                    bitmap?.Dispose();
                }
            }
        }
    }

    private IImage? ImageFor(string asset)
    {
        if (_images.TryGetValue(asset, out var bitmap))
        {
            return bitmap;
        }

        if (_page is { } page)
        {
            LoadImages(page);
        }

        return null;
    }

    private void ReleasePage()
    {
        foreach (var bitmap in _images.Values)
        {
            bitmap?.Dispose();
        }

        _images.Clear();
        _undo.Clear();
        _redo.Clear();
        _pendingConflict = null;
        _page = null;
        _canvas = null;
    }

    // A compact color choice: the color itself, named for screen readers and as a tooltip, so the widths
    // and all six colors share one row at the 980-DIP floor.
    private Control Swatch(string color, string name)
    {
        var selected = _inkColor == color;
        Button button = new()
        {
            Tag = "notes.color:" + name, MinWidth = 40, MinHeight = 44, Margin = new Thickness(0, 0, 8, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Ellipse
            {
                Width = 24, Height = 24, Fill = new SolidColorBrush(Color.Parse(color)),
                Stroke = ThemeBrush(selected ? "DeckTextBrush" : "DeckDividerBrush"), StrokeThickness = selected ? 3 : 1
            }
        };
        button.Classes.Set("primary", selected);
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) => SetColor(color);
        return button;
    }

    private IBrush ThemeBrush(string key)
    {
        return this.TryFindResource(key, out var value) && value is IBrush brush ? brush : Brushes.Gray;
    }

    // Whether a kept draft differs from the page: a keyboard closed without typing leaves nothing to resume.
    private static bool Unsaved(NotePage page, NotesTextDraft draft)
    {
        return draft.ElementId is null
            ? draft.Text.Length > 0
            : page.Elements.Find(item => item.Id == draft.ElementId)?.Text != draft.Text;
    }

    private static string FirstLine(string? text)
    {
        var line = (text ?? "").Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "Empty text" : line.Length > 80 ? line[..80] + "…" : line;
    }

    private static string? Count(int count, string noun)
    {
        return count == 0 ? null : count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }

    private sealed record InkStep(IReadOnlyList<NoteStroke> Strokes, IReadOnlyList<int>? Indices, bool Added);
}
