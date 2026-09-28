using System.Net;
using System.Text.Json;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Core.Animations;
using WSGM.Tests.Core.Themes;

namespace WSGM.Tests.Shell;

/// <summary>
///     The boot movies' one owner: what it publishes for the page, the section and the overlay, and how
///     a choice becomes the file Steam asks for.
/// </summary>
public sealed class AnimationServiceTests : IDisposable
{
    private readonly AnimationsConfig _config = new();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "WSGM.Tests.animsvc." + Guid.NewGuid().ToString("N"));

    private readonly List<Action<AnimationsConfig>> _writes = [];

    public AnimationServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "steam"));
    }

    private string Override =>
        Path.Combine(AnimationOverrides.Directory(Path.Combine(_root, "steam")), AnimationOverrides.BootFileName);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private AnimationLibrary Library()
    {
        var library = new AnimationLibrary(Path.Combine(_root, "library"));
        library.Add(AnimationLibraryTests.Listing("neon", "Neon"), new MemoryStream([1]));
        library.Add(AnimationLibraryTests.Listing("calm", "Calm"), new MemoryStream([2]));
        return library;
    }

    private AnimationService Service(HttpResponseMessage? answer = null)
    {
        var handler = new ThemeStoreClientTests.StubHandler(_ =>
            answer ?? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        return new AnimationService(
            Library(),
            new AnimationRepoClient(handler, "https://repo.example"),
            () => _config,
            change =>
            {
                _writes.Add(change);
                change(_config);
            },
            () => Path.Combine(_root, "steam"),
            new Random(3));
    }

    [Fact]
    public async Task AChoiceIsSavedCopiedToTheFileSteamAsksForAndAnnouncedAsNeedingARestart()
    {
        using var service = Service();
        service.Start();

        var applied = await service.SelectAsync("neon", CancellationToken.None);
        var unknown = await service.SelectAsync("nope", CancellationToken.None);

        Assert.True(applied.Succeeded);
        Assert.False(unknown.Succeeded);
        Assert.Equal("neon", _config.Boot);
        Assert.Single(_writes);
        Assert.Equal([1], File.ReadAllBytes(Override));
        var state = service.ReadState();
        Assert.True(state.Settings.RestartNeeded);
        Assert.Contains("Restart Steam", state.Notice!, StringComparison.Ordinal);
        Assert.Equal("neon", state.Selected);

        await service.SelectAsync("", CancellationToken.None);
        Assert.False(File.Exists(Override));
    }

    [Fact]
    public void StartWritesTheOverrideForTheSavedChoiceWithoutClaimingARestart()
    {
        _config.Boot = "calm";
        using var service = Service();

        service.Start();

        Assert.Equal([2], File.ReadAllBytes(Override));
        Assert.False(service.ReadState().Settings.RestartNeeded);
        Assert.Empty(_writes);
    }

    [Fact]
    public void ShuffleOnStartPicksTheMovieBeforeSteamReadsIt()
    {
        _config.ShuffleOnStart = true;
        _config.ShuffleExclusions = ["neon"];
        using var service = Service();

        service.Start();

        Assert.Equal("calm", _config.Boot);
        Assert.Single(_writes);
        Assert.Equal([2], File.ReadAllBytes(Override));
    }

    [Fact]
    public async Task RemovingThePlayingMovieReturnsBigPictureToSteamsOwn()
    {
        _config.Boot = "neon";
        using var service = Service();
        service.Start();

        var removed = await service.DeleteAsync("neon", CancellationToken.None);

        Assert.True(removed.Succeeded);
        Assert.Equal("", _config.Boot);
        Assert.False(File.Exists(Override));
        Assert.DoesNotContain(service.ReadState().Library, item => item.Id == "neon");
    }

    [Fact]
    public async Task TheRepositoryIsFetchedOnceForBrowseAndSortedAndSearchedLocally()
    {
        const string posts = """
                             {"posts":[
                               {"id":"neon","title":"Neon","type":"boot_video","likes":1,"downloads":5,"updated_at":"2025-01-01T00:00:00Z"},
                               {"id":"new1","title":"Aurora","type":"boot_video","likes":9,"downloads":1,"updated_at":"2025-06-01T00:00:00Z"},
                               {"id":"s2","title":"Calm night","type":"suspend_video","likes":3,"downloads":7,"updated_at":"2024-01-01T00:00:00Z"}
                             ]}
                             """;
        using var service =
            Service(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(posts) });
        service.Start();
        TaskCompletionSource fetched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            if (service.ReadState().Browse is { Loading: false, Total: > 0 })
            {
                fetched.TrySetResult();
            }
        };

        await service.BrowseAsync("Newest", "", CancellationToken.None);
        await fetched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var browse = service.ReadState().Browse;
        Assert.Equal(2, browse.Total);
        Assert.Equal(["Aurora", "Neon"], browse.Items.Select(item => item.Name));
        Assert.True(browse.Items[1].Downloaded, "the library's copy is marked");
        Assert.Equal("2025-06-01", browse.Items[0].Updated);

        await service.BrowseAsync("Most popular", "", CancellationToken.None);
        Assert.Equal(["Neon", "Aurora"], service.ReadState().Browse.Items.Select(item => item.Name));
        await service.BrowseAsync("Alphabetical", "aur", CancellationToken.None);
        Assert.Equal(["Aurora"], service.ReadState().Browse.Items.Select(item => item.Name));

        await service.OpenAsync("new1", CancellationToken.None);
        Assert.Equal("Aurora", service.ReadState().Detail!.Name);
        Assert.False(service.ReadState().Detail!.Downloaded);
    }

    [Fact]
    public async Task TheQuickAccessSectionOffersTheChoiceAndMapsANameBackToItsId()
    {
        using var service = Service();
        service.Start();

        var item = service.ReadExtensionsItem();

        Assert.Equal("wsgm.animations", item.Id);
        Assert.Equal(["Browse movies…", "Library…", "Shuffle"], item.Actions!.Select(action => action.Label));
        var settings = item.Settings!;
        Assert.Equal(("boot", "Steam's own"), (settings[0].Key, settings[0].TextValue));
        Assert.Equal(["Steam's own", "Calm", "Neon"], settings[0].Choices);
        Assert.Equal(("shuffleOnStart", false), (settings[1].Key, settings[1].BooleanValue));

        var chosen = await service.ConfigureExtensionAsync("boot", JsonSerializer.SerializeToElement("Calm"),
            CancellationToken.None);
        Assert.True(chosen.Succeeded);
        Assert.Equal("calm", _config.Boot);
        Assert.Equal("Calm", service.ReadExtensionsItem().Settings![0].TextValue);
        var stale = await service.ConfigureExtensionAsync("boot", JsonSerializer.SerializeToElement("Gone"),
            CancellationToken.None);
        Assert.False(stale.Succeeded);

        var route = await service.ActivateExtensionAsync("wsgm.animations.manage", CancellationToken.None);
        Assert.True(route.Succeeded);
        Assert.Equal("/wsgm/animations", route.Payload!.Value.GetProperty("route").GetString());
        Assert.Equal("library", service.ReadState().ActiveTab);
    }

    [Fact]
    public async Task ShuffleAndTheStartSettingAreTheSectionsToo()
    {
        using var service = Service();
        service.Start();

        var shuffled = await service.ActivateExtensionAsync("wsgm.animations.shuffle", CancellationToken.None);
        var setting = await service.ConfigureExtensionAsync("shuffleOnStart",
            JsonSerializer.SerializeToElement(true), CancellationToken.None);

        Assert.True(shuffled.Succeeded);
        Assert.True(setting.Succeeded);
        Assert.Contains(_config.Boot, new[] { "neon", "calm" });
        Assert.True(_config.ShuffleOnStart);
        Assert.True(service.ReadState().Settings.ShuffleOnStart);
    }
}
