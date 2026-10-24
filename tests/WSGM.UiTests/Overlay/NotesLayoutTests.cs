using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

/// <summary>The Overlay's Notes notebook, page, Ink tools and text editor at handheld sizes.</summary>
public sealed class NotesLayoutTests
{
    [AvaloniaTheory]
    [InlineData(NotesInkTool.Pen)]
    [InlineData(NotesInkTool.Highlighter)]
    public async Task TouchInkIsSavedWhenTheFingerLiftsAndCanBeUndone(NotesInkTool tool)
    {
        using var fixture = new UiFixture();
        var (service, page) = SeededNotes(fixture);
        service.OverlayLocation = new NotesOverlayLocation(NoteScope.Global, page.Id, "ink", 1, 0);
        var window = OpenNotes(fixture, service, 980, 640);
        var canvas = Assert.Single(window.GetVisualDescendants().OfType<NotesCanvas>());
        canvas.InkTool = tool;
        List<NoteStroke> completed = [];
        canvas.StrokeCompleted += completed.Add;
        var start = canvas.TranslatePoint(new Point(40, 40), window)!.Value;
        var end = canvas.TranslatePoint(new Point(180, 100), window)!.Value;

        using var touch = window.TouchBegin(start);
        window.TouchMove(touch, end);
        window.TouchEnd(touch, end);

        var stroke = Assert.Single(completed);
        Assert.True(stroke.Points.Count >= 4);
        Assert.Equal(tool == NotesInkTool.Highlighter ? NoteStroke.HighlighterTool : NoteStroke.PenTool, stroke.Tool);
        await AsyncConditions.WaitForAsync(() => canvas.Page?.Strokes.Count == page.Strokes.Count + 1);
        var stored = new NoteStore(Path.Combine(fixture.Store.Context.Root, "notes")).Load();
        Assert.Contains(stored.Pages.Single(saved => saved.Id == page.Id).Strokes, saved => saved.Id == stroke.Id);
        DevicePageCaptureTests.Capture(window, Path.Combine(CaptureDirectory(980, 640), "touch-" + tool + ".png"));

        UiFixture.Click(window, Tagged(window, "notes.undo"));
        await AsyncConditions.WaitForAsync(() => canvas.Page?.Strokes.Count == page.Strokes.Count);
        Assert.DoesNotContain(service.ReadPage(page.Id)!.Strokes, saved => saved.Id == stroke.Id);
    }

    [AvaloniaFact]
    public async Task TouchEraserCommitsWhenTheFingerLiftsAndUndoRestoresTheStroke()
    {
        using var fixture = new UiFixture();
        var (service, page) = SeededNotes(fixture);
        service.OverlayLocation = new NotesOverlayLocation(NoteScope.Global, page.Id, "ink", 1, 0);
        var window = OpenNotes(fixture, service, 980, 640);
        var canvas = Assert.Single(window.GetVisualDescendants().OfType<NotesCanvas>());
        canvas.InkTool = NotesInkTool.Eraser;
        var scale = canvas.Bounds.Width / NotePage.PageWidth;
        var point = canvas.TranslatePoint(new Point(40 * scale, 70 * scale), window)!.Value;
        List<string> erased = [];
        canvas.StrokesErased += ids => erased.AddRange(ids);

        using var touch = window.TouchBegin(point);
        window.TouchEnd(touch, point);

        Assert.Contains(page.Strokes[1].Id, erased);
        await AsyncConditions.WaitForAsync(() => canvas.Page?.Strokes.Count == page.Strokes.Count - 1);
        UiFixture.Click(window, Tagged(window, "notes.undo"));
        await AsyncConditions.WaitForAsync(() => canvas.Page?.Strokes.Count == page.Strokes.Count);
        Assert.Equal(page.Strokes.Select(stroke => stroke.Id), service.ReadPage(page.Id)!.Strokes.Select(stroke => stroke.Id));
    }

    [AvaloniaFact]
    public void CancellingATouchContactDoesNotSaveAnUnfinishedStroke()
    {
        using var fixture = new UiFixture();
        var (service, page) = SeededNotes(fixture);
        service.OverlayLocation = new NotesOverlayLocation(NoteScope.Global, page.Id, "ink", 1, 0);
        var window = OpenNotes(fixture, service, 980, 640);
        var canvas = Assert.Single(window.GetVisualDescendants().OfType<NotesCanvas>());
        List<NoteStroke> completed = [];
        canvas.StrokeCompleted += completed.Add;
        var start = canvas.TranslatePoint(new Point(40, 40), window)!.Value;
        var end = canvas.TranslatePoint(new Point(180, 100), window)!.Value;
        var touch = window.TouchBegin(start);
        window.TouchMove(touch, end);

        touch.Dispose();

        Assert.Empty(completed);
        Assert.Equal(page.Strokes.Count, service.ReadPage(page.Id)!.Strokes.Count);
    }

    [AvaloniaTheory]
    [InlineData(980, 640)]
    [InlineData(1280, 800)]
    public void ScreenshotFlowExplainsDesktopCaptureAndKeepsStartReachable(int width, int height)
    {
        using var fixture = new UiFixture();
        var (service, page) = SeededNotes(fixture);
        service.OverlayLocation = new NotesOverlayLocation(NoteScope.Global, page.Id, "navigate", 1, 0);
        var window = OpenNotes(fixture, service, width, height);
        var notes = UiFixture.Named<NotesView>(window, "NotesHost");
        string? capturedPage = null;
        notes.StartCapture = (id, _) =>
        {
            capturedPage = id;
            return SteamUiToolkit.SteamUiCommandResult.Applied;
        };
        service.Publish();
        Dispatcher.UIThread.RunJobs();

        UiFixture.Click(window, Tagged(window, "notes.add.screenshot"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text?.Contains("desktop", StringComparison.Ordinal) == true);
        var start = Tagged(window, "notes.capture.start");
        Assert.True(start.IsEffectivelyVisible);
        DevicePageCaptureTests.Capture(window, Path.Combine(CaptureDirectory(width, height), "screenshot.png"));
        UiFixture.Click(window, start);
        Assert.Equal(page.Id, capturedPage);
    }

    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1024, 640)]
    [InlineData(980, 640)]
    public void APageWithTextInkAndAScreenshotIsDrawnAndItsModesAreReachable(int width, int height)
    {
        using var fixture = new UiFixture();
        var (service, page) = SeededNotes(fixture);
        service.OverlayLocation = new NotesOverlayLocation(NoteScope.Global, page.Id, "navigate", 1, 0);
        var window = OpenNotes(fixture, service, width, height);
        var canvas = Assert.Single(window.GetVisualDescendants().OfType<NotesCanvas>());
        Assert.Equal(page.Id, canvas.Page?.Id);
        Assert.True(UiFixture.Named<ScrollViewer>(window, "ContentScroller").Bounds.Width >= width * 0.7,
            "Notes should have at least 70% of the window width.");
        var asset = page.Elements.Single(element => element.Kind == NoteElement.ImageKind).Asset!;
        DrainUntilDecoded(canvas, asset);
        var directory = CaptureDirectory(width, height);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "page.png"));
        Assert.True(canvas.TranslatePoint(new Point(0, canvas.Bounds.Height), window)?.Y <= height - 65,
            "The canvas must fit above the Overlay footer.");
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), control => Equals(control.Tag, "notes.delete"));
        var notes = UiFixture.Named<NotesView>(window, "NotesHost");
        UiFixture.Click(window, Tagged(window, "notes.options"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(Tagged(window, "notes.pin").IsEffectivelyVisible);
        Assert.True(Tagged(window, "notes.delete").IsEffectivelyVisible);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "options.png"));
        notes.Back();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(canvas, Assert.Single(window.GetVisualDescendants().OfType<NotesCanvas>()));
        UiFixture.Click(window, Tagged(window, "notes.options"));
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, Tagged(window, "notes.contents"));
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, Tagged(window, "notes.element:" + page.Elements[0].Id));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(page.Elements[0].Id, canvas.SelectedElement);
        Assert.Same(canvas, Assert.Single(window.GetVisualDescendants().OfType<NotesCanvas>()));

        UiFixture.Click(window, Tagged(window, "notes.mode.ink"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(NotesCanvasMode.Ink, canvas.Mode);
        Assert.True(Tagged(window, "notes.tool.eraser").IsEffectivelyVisible);
        File.WriteAllLines(Path.Combine(directory, "ink-layout.txt"),
            new[] { "notes.mode.ink", "notes.add.text", "notes.add.screenshot", "notes.options" }.Select(tag =>
            {
                var control = Tagged(window, tag);
                return $"{tag}: visible={control.IsEffectivelyVisible} bounds={control.Bounds} at={control.TranslatePoint(default, window)} parent={(control.Parent as Control)?.Bounds}";
            }));
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "ink.png"));

        UiFixture.Click(window, Tagged(window, "notes.add.text"));
        Dispatcher.UIThread.RunJobs();
        // The overlay's own full-width keyboard surface, multiline.
        Assert.True(window.HasActiveSurface);
        var keyboard = Assert.Single(window.GetVisualDescendants().OfType<KeyboardPanel>());
        var editor = Assert.Single(keyboard.GetVisualDescendants().OfType<TextBox>());
        Assert.True(editor.AcceptsReturn);
        editor.Text = "Left door\nthen the stairs";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Left door\nthen the stairs", service.OverlayTextDraft?.Text);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "text-editor.png"));
    }

    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1024, 640)]
    [InlineData(980, 640)]
    public void TheNotebookListsItsPagesAndOffersANewPage(int width, int height)
    {
        using var fixture = new UiFixture();
        var (service, page) = SeededNotes(fixture);
        service.SetPinned(page.Id, true);
        service.CreatePage(NoteScope.Global, "Boss strategy and routes for the final area");
        service.CreatePage(NoteScope.Global, "Next session");
        var window = OpenNotes(fixture, service, width, height);

        Assert.True(Tagged(window, "notes.create").IsEffectivelyVisible);
        Assert.True(Tagged(window, "notes.page:" + page.Id).IsEffectivelyVisible);
        Assert.Empty(window.GetVisualDescendants().OfType<NotesCanvas>());
        Assert.Equal(3, window.GetVisualDescendants().OfType<StackPanel>().Count(panel => panel.Tag is string tag && tag.StartsWith("notes.card:", StringComparison.Ordinal)));
        DevicePageCaptureTests.Capture(window, Path.Combine(CaptureDirectory(width, height), "notebook.png"));
        var notesWidth = UiFixture.Named<ScrollViewer>(window, "ContentScroller").Bounds.Width;
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemTools));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(notesWidth, UiFixture.Named<ScrollViewer>(window, "ContentScroller").Bounds.Width);
    }

    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1024, 640)]
    [InlineData(980, 640)]
    public void EmptyNotebookExplainsWhatNewPagesCanContain(int width, int height)
    {
        using var fixture = new UiFixture();
        var service = new NotesService(new NoteStore(Path.Combine(fixture.Store.Context.Root, "notes")), null, null, null);
        service.EnsureLoaded();
        var window = OpenNotes(fixture, service, width, height);
        Assert.True(Tagged(window, "notes.create").IsEffectivelyVisible);
        Assert.Empty(window.GetVisualDescendants().OfType<NotesCanvas>());
        DevicePageCaptureTests.Capture(window, Path.Combine(CaptureDirectory(width, height), "empty.png"));
    }

    private static OverlayWindow OpenNotes(UiFixture fixture, NotesService service, int width, int height)
    {
        var window = fixture.Overlay(width, height, session: new OverlayWindow.SessionState
        {
            Destination = OverlayDestination.System
        });
        window.AttachNotes(service);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.Notes));
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiFixture.Named<NotesView>(window, "NotesHost").IsEffectivelyVisible);
        return window;
    }

    private static (NotesService Service, NotePage Page) SeededNotes(UiFixture fixture)
    {
        var store = new NoteStore(Path.Combine(fixture.Store.Context.Root, "notes"));
        var asset = store.AddAsset(SamplePng());
        NotePage page = new()
        {
            Id = NoteDocumentRules.NewId(), Scope = NoteScope.Global, Title = "Lighthouse puzzle", Revision = 1,
            Created = DateTimeOffset.UnixEpoch, Modified = DateTimeOffset.UnixEpoch,
            Elements =
            [
                new NoteElement
                {
                    Id = NoteDocumentRules.NewId(), Kind = NoteElement.TextKind, X = 24, Y = 24, Width = 560,
                    Height = 120, Text = "Dial order: 3 - 1 - 4\nThe red lamp faces north."
                },
                new NoteElement
                {
                    Id = NoteDocumentRules.NewId(), Kind = NoteElement.ImageKind, X = 640, Y = 24, Width = 560,
                    Height = 315, Asset = asset, PixelWidth = 320, PixelHeight = 180
                }
            ],
            Strokes =
            [
                new NoteStroke
                {
                    Id = NoteDocumentRules.NewId(), Color = "#FF5A5A", Width = 6,
                    Points = [700, 120, 760, 160, 820, 140, 880, 220]
                },
                new NoteStroke
                {
                    Id = NoteDocumentRules.NewId(), Tool = NoteStroke.HighlighterTool, Color = "#FFD93D", Width = 18,
                    Points = [40, 70, 400, 70]
                }
            ]
        };
        store.WritePage(page);
        var service = new NotesService(store, null, null, null);
        Assert.True(service.EnsureLoaded());
        return (service, page);
    }

    private static byte[] SamplePng()
    {
        using WriteableBitmap bitmap = new(new PixelSize(320, 180), new Vector(96, 96), PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            var pixels = new byte[buffer.RowBytes * buffer.Size.Height];
            for (var y = 0; y < buffer.Size.Height; y++)
            {
                for (var x = 0; x < buffer.Size.Width; x++)
                {
                    var index = y * buffer.RowBytes + x * 4;
                    pixels[index] = (byte)(x * 255 / buffer.Size.Width);
                    pixels[index + 1] = (byte)(y * 255 / buffer.Size.Height);
                    pixels[index + 2] = 90;
                    pixels[index + 3] = 255;
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        }

        using MemoryStream stream = new();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    // Screenshots decode off the UI thread and arrive as a dispatcher job; drain until this one has.
    private static void DrainUntilDecoded(NotesCanvas canvas, string asset)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (canvas.ImageSource?.Invoke(asset) is null && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Yield();
        }

        Assert.NotNull(canvas.ImageSource?.Invoke(asset));
    }

    private static Control Tagged(Window window, string tag)
    {
        return window.GetVisualDescendants().OfType<Control>().First(control => Equals(control.Tag, tag));
    }

    private static string CaptureDirectory(int width, int height)
    {
        var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", $"notes-{width}x{height}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
