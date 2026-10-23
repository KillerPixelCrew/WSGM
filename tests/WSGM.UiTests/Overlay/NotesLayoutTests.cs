using Avalonia;
using Avalonia.Controls;
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
    [InlineData(1280, 800)]
    [InlineData(1024, 640)]
    public void APageWithTextInkAndAScreenshotIsDrawnAndItsModesAreReachable(int width, int height)
    {
        using var fixture = new UiFixture();
        var (service, page) = SeededNotes(fixture);
        service.OverlayLocation = new NotesOverlayLocation(NoteScope.Global, page.Id, "navigate", 1, 0);
        var window = OpenNotes(fixture, service, width, height);
        var canvas = Assert.Single(window.GetVisualDescendants().OfType<NotesCanvas>());
        Assert.Equal(page.Id, canvas.Page?.Id);
        var asset = page.Elements.Single(element => element.Kind == NoteElement.ImageKind).Asset!;
        DrainUntilDecoded(canvas, asset);
        var directory = CaptureDirectory(width, height);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "page.png"));

        UiFixture.Click(window, Tagged(window, "notes.mode.ink"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(NotesCanvasMode.Ink, canvas.Mode);
        Assert.True(Tagged(window, "notes.tool.eraser").IsEffectivelyVisible);
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
    public void TheNotebookListsItsPagesAndOffersANewPage(int width, int height)
    {
        using var fixture = new UiFixture();
        var (service, page) = SeededNotes(fixture);
        var window = OpenNotes(fixture, service, width, height);

        Assert.True(Tagged(window, "notes.create").IsEffectivelyVisible);
        Assert.True(Tagged(window, "notes.page:" + page.Id).IsEffectivelyVisible);
        DevicePageCaptureTests.Capture(window, Path.Combine(CaptureDirectory(width, height), "notebook.png"));
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
