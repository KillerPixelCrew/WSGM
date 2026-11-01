using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>The Notes files: one page per file, content-addressed images and safe recovery.</summary>
public sealed class NoteStoreTests : IDisposable
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsgm-notes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static NotePage Page(string scope = NoteScope.Global)
    {
        return new NotePage
        {
            Id = NoteDocumentRules.NewId(), Scope = scope, Title = "Clues", Revision = 1,
            Created = DateTimeOffset.UnixEpoch, Modified = DateTimeOffset.UnixEpoch,
            Elements =
            [
                new NoteElement
                {
                    Id = NoteDocumentRules.NewId(), Kind = NoteElement.TextKind, X = 10, Y = 10, Width = 200,
                    Height = 80, Text = "Code: 4-1-7\nLeft door"
                }
            ],
            Strokes =
            [
                new NoteStroke { Id = NoteDocumentRules.NewId(), Color = "#FF0000", Width = 4, Points = [1, 2, 3, 4] }
            ]
        };
    }

    [Fact]
    public void APageRoundTripsWithItsTextInkAndPositions()
    {
        var store = new NoteStore(_root);
        var page = Page("steam:1145360");
        store.WritePage(page);

        var loaded = Assert.Single(new NoteStore(_root).Load().Pages);

        Assert.Equal(page.Id, loaded.Id);
        Assert.Equal("steam:1145360", loaded.Scope);
        Assert.Equal("Code: 4-1-7\nLeft door", Assert.Single(loaded.Elements).Text);
        Assert.Equal([1f, 2f, 3f, 4f], Assert.Single(loaded.Strokes).Points);
        Assert.Equal(NotePage.CurrentFormat, loaded.Format);
    }

    [Fact]
    public void AnUnparsablePageIsSetAsideNotOverwritten()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pages"));
        var broken = Path.Combine(_root, "pages", NoteDocumentRules.NewId() + ".json");
        File.WriteAllText(broken, "{ not json");

        var snapshot = new NoteStore(_root).Load();

        Assert.Empty(snapshot.Pages);
        var aside = Assert.Single(snapshot.SetAside);
        Assert.False(File.Exists(broken));
        Assert.Equal("{ not json", File.ReadAllText(aside));
    }

    [Fact]
    public void APageFromANewerFormatIsListedButMarkedReadOnly()
    {
        var store = new NoteStore(_root);
        var page = Page();
        page.Format = NotePage.CurrentFormat + 1;
        store.WritePage(page);

        var snapshot = new NoteStore(_root).Load();

        Assert.Single(snapshot.Pages);
        Assert.Contains(page.Id, snapshot.ReadOnlyPages);
    }

    [Fact]
    public void ImagesAreContentAddressedAndOnlyPngOrJpegAreAccepted()
    {
        var store = new NoteStore(_root);

        var first = store.AddAsset(Png);
        var again = store.AddAsset(Png);
        var jpeg = store.AddAsset(Jpeg);

        Assert.Equal(first, again);
        Assert.EndsWith(".png", first, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", jpeg, StringComparison.Ordinal);
        Assert.Equal(Png, store.ReadAsset(first));
        Assert.Throws<NoteStoreException>(() => store.AddAsset([1, 2, 3, 4]));
    }

    [Fact]
    public void LoadRemovesOnlyUnreferencedImagesItNamed()
    {
        var store = new NoteStore(_root);
        var kept = store.AddAsset(Png);
        var orphan = store.AddAsset(Jpeg);
        var foreign = Path.Combine(_root, "assets", "readme.txt");
        File.WriteAllText(foreign, "not ours");
        var page = Page();
        page.Elements.Add(new NoteElement
        {
            Id = NoteDocumentRules.NewId(), Kind = NoteElement.ImageKind, X = 0, Y = 200, Width = 640, Height = 360,
            Asset = kept, PixelWidth = 1920, PixelHeight = 1080
        });
        store.WritePage(page);

        new NoteStore(_root).Load();

        Assert.True(File.Exists(store.AssetPath(kept)));
        Assert.False(File.Exists(store.AssetPath(orphan)));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void SettingsKeepTheCountdownWithinFiveToTenSeconds()
    {
        var store = new NoteStore(_root);
        store.WriteSettings(new NoteSettings { CaptureCountdownSeconds = 60 });

        Assert.Equal(NoteSettings.MaximumCountdown, new NoteStore(_root).Load().Settings.CaptureCountdownSeconds);
    }

    [Theory]
    [InlineData("global", true)]
    [InlineData("steam:1145360", true)]
    [InlineData("library:xbox:Microsoft.Game_8wekyb3d8bbwe!App", true)]
    [InlineData("process:game.exe", true)]
    [InlineData("steam:0", false)]
    [InlineData("library:xbox:", false)]
    [InlineData("Hades", false)]
    [InlineData("", false)]
    public void ScopesAreStableIdentitiesNeverTitles(string scope, bool valid)
    {
        Assert.Equal(valid, NoteScope.IsValid(scope));
    }

    [Fact]
    public void ValidationRejectsNonFiniteGeometryButNotLongContent()
    {
        var page = Page();
        page.Elements[0].Text = new string('x', 200_000);
        Assert.True(NoteDocumentRules.TryValidate(page, out _));

        page.Elements[0].X = double.NaN;
        Assert.False(NoteDocumentRules.TryValidate(page, out _));
    }

    [Fact]
    public void TheEraserFindsStrokesAlongTheirSegments()
    {
        var page = Page();
        page.Strokes =
        [
            new NoteStroke { Id = NoteDocumentRules.NewId(), Width = 4, Points = [0, 0, 100, 0] },
            new NoteStroke { Id = NoteDocumentRules.NewId(), Width = 4, Points = [0, 50, 100, 50] }
        ];

        var hits = NoteDocumentRules.StrokesAt(page, 50, 3, 2);

        Assert.Equal([page.Strokes[0].Id], hits);
    }

    [Fact]
    public void SimplifyKeepsTheEndsAndDropsInvisiblePoints()
    {
        float[] raw = [0, 0, 0.1f, 0, 0.2f, 0, 10, 0, 10.1f, 0];

        var (points, pressure) = NoteDocumentRules.Simplify(raw, [], 4);

        Assert.Equal([0f, 0f, 10f, 0f, 10.1f, 0f], points);
        Assert.Empty(pressure);
    }
}
