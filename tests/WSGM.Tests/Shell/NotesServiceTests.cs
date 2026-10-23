using System.Text.Json;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

/// <summary>The shared Notes owner: revisions, conflicts, identity and both surfaces' commands.</summary>
public sealed class NotesServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsgm-notes-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-10-23T12:00:00Z"));
    private RunningApplicationTargetSnapshot _running = RunningApplicationTargetSnapshot.Initial();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private NotesService Create(Func<uint, (string, string, string)?>? library = null)
    {
        var service = new NotesService(new NoteStore(_root), () => _running, library, null, _time);
        Assert.True(service.EnsureLoaded());
        return service;
    }

    private static RunningApplicationTargetSnapshot Running(RunningApplicationTargetState state, uint? appId = null,
        string? application = null)
    {
        return new RunningApplicationTargetSnapshot(1, 1, state, application ?? (appId is { } id ? "steam:" + id : null),
            appId, null, "game.exe", null);
    }

    [Fact]
    public void AnEditStartsFromARevisionAndAStaleEditIsRefusedWithTheNewestPage()
    {
        var service = Create();
        var page = service.CreatePage("steam:1145360").Page!;
        var steam = service.AddText(page.Id, page.Revision, 10, 10, "From Steam");
        Assert.True(steam.Succeeded);

        // The overlay still shows revision 1.
        var stale = service.AddText(page.Id, page.Revision, 20, 20, "From the overlay");

        Assert.Equal(NoteEditStatus.Conflict, stale.Status);
        Assert.Equal(2, stale.Page!.Revision);
        var current = service.ReadPage(page.Id)!;
        Assert.Equal("From Steam", Assert.Single(current.Elements).Text);
    }

    [Fact]
    public void ChangesSurviveARestartAndCreateNoDuplicates()
    {
        var service = Create();
        var page = service.CreatePage(NoteScope.Global, "Shopping").Page!;
        service.AddText(page.Id, 1, 0, 0, "Iron x4");

        var restarted = Create();

        var listed = Assert.Single(restarted.ReadPages(NoteScope.Global));
        Assert.Equal("Shopping", listed.Title);
        Assert.Equal(2, listed.Revision);
    }

    [Fact]
    public void PinningAndOrderingDoNotInvalidateAnOpenDraft()
    {
        var service = Create();
        var first = service.CreatePage(NoteScope.Global, "First").Page!;
        var second = service.CreatePage(NoteScope.Global, "Second").Page!;

        Assert.True(service.SetPinned(first.Id, true).Succeeded);
        Assert.True(service.MovePage(second.Id, 1).Succeeded);

        Assert.Equal(1, service.ReadPage(first.Id)!.Revision);
        Assert.True(service.RenamePage(first.Id, 1, "Renamed").Succeeded);
        Assert.Equal("Renamed", service.ReadPages(NoteScope.Global)[0].Title);
    }

    [Fact]
    public void UndoOfAnEraseRestoresStrokesInTheirOriginalOrder()
    {
        var service = Create();
        var page = service.CreatePage(NoteScope.Global).Page!;
        NoteStroke Stroke(float y)
        {
            return new NoteStroke { Id = NoteDocumentRules.NewId(), Points = [0, y, 10, y] };
        }

        var strokes = new[] { Stroke(0), Stroke(10), Stroke(20) };
        var revision = page.Revision;
        foreach (var stroke in strokes)
        {
            revision = service.AddStrokes(page.Id, revision, [stroke]).Page!.Revision;
        }

        revision = service.RemoveStrokes(page.Id, revision, new HashSet<string> { strokes[1].Id }).Page!.Revision;
        var restored = service.AddStrokes(page.Id, revision, [strokes[1]], [1]).Page!;

        Assert.Equal(strokes.Select(stroke => stroke.Id), restored.Strokes.Select(stroke => stroke.Id));
    }

    [Fact]
    public void DeletingAnImageKeepsThePage()
    {
        var service = Create();
        var page = service.CreatePage(NoteScope.Global).Page!;
        var text = service.AddText(page.Id, 1, 0, 0, "Keep me");

        var removed = service.RemoveElement(page.Id, text.Page!.Revision, text.ElementId!);

        Assert.True(removed.Succeeded);
        Assert.NotNull(service.ReadPage(page.Id));
    }

    [Fact]
    public void AStaleDeleteIsAConflictAndDeletesNothing()
    {
        var service = Create();
        var page = service.CreatePage(NoteScope.Global).Page!;
        service.AddText(page.Id, 1, 0, 0, "Newer");

        Assert.Equal(NoteEditStatus.Conflict, service.DeletePage(page.Id, 1).Status);
        Assert.NotNull(service.ReadPage(page.Id));
    }

    [Fact]
    public void AnImportedShortcutKeepsItsNotebookAcrossAShortcutChange()
    {
        var service = Create(appId => appId is 3000000001u or 3000000002u ? ("xbox", "Game!App", "Forza") : null);

        var before = service.ResolveSteamApp(3000000001u);
        var after = service.ResolveSteamApp(3000000002u);

        Assert.Equal("library:xbox:Game!App", before.Scope);
        Assert.Equal(before.Scope, after.Scope);
        Assert.Equal("Forza", after.Title);
    }

    [Fact]
    public void AnUnresolvedGameIsUnknownNeverGlobal()
    {
        var service = Create();

        _running = Running(RunningApplicationTargetState.Ambiguous);
        var ambiguous = service.ResolveCurrentGame();
        _running = Running(RunningApplicationTargetState.Global);
        var none = service.ResolveCurrentGame();
        _running = Running(RunningApplicationTargetState.Active, 1145360);
        var game = service.ResolveCurrentGame();

        Assert.Equal(NotesGameState.Unknown, ambiguous.State);
        Assert.Null(ambiguous.Scope);
        Assert.Equal(NotesGameState.None, none.State);
        Assert.Equal("steam:1145360", game.Scope);
    }

    [Fact]
    public void AFailedWriteRefusesTheEditAndKeepsNothing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "pages"), "a file where the folder belongs");
        var service = new NotesService(new NoteStore(_root), null, null, null, _time);
        Assert.True(service.EnsureLoaded());

        var result = service.CreatePage(NoteScope.Global);

        Assert.Equal(NoteEditStatus.Refused, result.Status);
        Assert.Empty(service.ReadPages(NoteScope.Global));
    }

    [Fact]
    public void NothingIsEditableBeforeTheStoreLoads()
    {
        var service = new NotesService(new NoteStore(_root), null, null, null, _time);

        Assert.Equal(NoteEditStatus.Refused, service.CreatePage(NoteScope.Global).Status);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void SteamPublishesNothingHeavyUntilItsPageOpens()
    {
        var service = new NotesService(new NoteStore(_root), () => _running, null, null, _time);

        var state = NotesSteamSurface.ReadState(service);

        Assert.False(state.Loaded);
        Assert.False(service.Loaded);
        Assert.Empty(state.Pages);
    }

    [Fact]
    public void SteamRequestsRejectUnknownFieldsAndWrongTypes()
    {
        Assert.True(NotesSteamSurface.TryRead(Json("{\"pageId\":\"x\",\"revision\":2}"), out var request));
        Assert.Equal(2, request.Revision);
        Assert.False(NotesSteamSurface.TryRead(Json("{\"pageId\":\"x\",\"script\":\"alert(1)\"}"), out _));
        Assert.False(NotesSteamSurface.TryRead(Json("{\"revision\":\"two\"}"), out _));
    }

    [Fact]
    public void SteamOpensOnTheOverlaysOwnGameAndShowsItsPages()
    {
        var service = Create();
        var page = service.CreatePage("steam:1145360", "Boss").Page!;
        service.SteamView.SetWindow(1145360, 4321);
        service.SteamView.Select("steam:1145360", page.Id);

        var state = NotesSteamSurface.ReadState(service);

        Assert.True(state.Loaded);
        Assert.Equal("steam:1145360", state.Scope);
        Assert.Equal(page.Id, state.Page!.Id);
        Assert.True(state.CaptureAvailable);
        // The published page carries no image bytes; images are requested one at a time.
        Assert.DoesNotContain("data:image", JsonSerializer.Serialize(state, NotesSteamJsonContext.Default.NotesSteamState));
    }

    private static JsonElement Json(string text)
    {
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
