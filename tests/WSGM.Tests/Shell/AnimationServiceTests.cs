using System.Net;
using System.Text.Json;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Core.Animations;
using WSGM.Tests.Core.Themes;

namespace WSGM.Tests.Shell;

/// <summary>
///     The animations' one owner: what it publishes for the page, the section and the overlay, and how
///     a slot's choice becomes the file Steam asks for.
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

    private string Overrides => AnimationOverrides.Directory(Path.Combine(_root, "steam"));

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
        library.Add(AnimationLibraryTests.Listing("boot1", name: "Neon"), new MemoryStream([1]));
        library.Add(AnimationLibraryTests.Listing("sus1", AnimationTargets.Suspend, "Calm"), new MemoryStream([2]));
        return library;
    }

    private AnimationService Service(AnimationLibrary? library = null, HttpResponseMessage? answer = null)
    {
        var handler = new ThemeStoreClientTests.StubHandler(_ =>
            answer ?? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        return new AnimationService(
            library ?? Library(),
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
    public async Task ASlotChoiceIsSavedCopiedToTheFileSteamAsksForAndAnnouncedAsNeedingARestart()
    {
        using var service = Service();
        service.Start();
        var changes = 0;
        service.Changed += () => changes++;

        var applied = await service.SetSlotAsync(AnimationSlots.Boot, "boot1", CancellationToken.None);
        var wrong = await service.SetSlotAsync(AnimationSlots.Suspend, "boot1", CancellationToken.None);
        var unknown = await service.SetSlotAsync(AnimationSlots.Suspend, "nope", CancellationToken.None);

        Assert.True(applied.Succeeded);
        Assert.False(wrong.Succeeded);
        Assert.False(unknown.Succeeded);
        Assert.Equal("boot1", _config.Boot);
        Assert.Single(_writes);
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(Overrides, "bigpicture_startup.webm")));
        var state = service.ReadState();
        Assert.True(state.Settings.RestartNeeded);
        Assert.Contains("Restart Steam", state.Notice!, StringComparison.Ordinal);
        Assert.Equal("boot1", state.Slots[AnimationSlots.Boot]);
        Assert.True(changes >= 1);

        await service.SetSlotAsync(AnimationSlots.Boot, "", CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(Overrides, "bigpicture_startup.webm")));
    }

    [Fact]
    public void StartWritesTheOverridesForTheSavedChoicesWithoutClaimingARestart()
    {
        _config.Suspend = "sus1";
        _config.Throbber = "sus1";
        using var service = Service();

        service.Start();

        Assert.Equal([2], File.ReadAllBytes(Path.Combine(Overrides, "steam_os_suspend.webm")));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(Overrides, "steam_os_suspend_from_throbber.webm")));
        Assert.False(service.ReadState().Settings.RestartNeeded);
        Assert.Empty(_writes);
    }

    [Fact]
    public void ShuffleOnStartPicksEverySlotBeforeSteamReadsThem()
    {
        _config.ShuffleOnStart = true;
        using var service = Service();

        service.Start();

        Assert.Equal("boot1", _config.Boot);
        Assert.Equal("sus1", _config.Suspend);
        Assert.Equal("sus1", _config.Throbber);
        Assert.Single(_writes);
        Assert.True(File.Exists(Path.Combine(Overrides, "bigpicture_startup.webm")));
    }

    [Fact]
    public async Task RemovingAnAnimationFreesItsSlot()
    {
        _config.Boot = "boot1";
        using var service = Service();
        service.Start();

        var removed = await service.DeleteAsync("boot1", CancellationToken.None);

        Assert.True(removed.Succeeded);
        Assert.Equal("", _config.Boot);
        Assert.False(File.Exists(Path.Combine(Overrides, "bigpicture_startup.webm")));
        Assert.DoesNotContain(service.ReadState().Library, item => item.Id == "boot1");
    }

    [Fact]
    public async Task TheRepositoryIsFetchedOnceForBrowseAndFilteredSortedAndSearchedLocally()
    {
        const string posts = """
                             {"posts":[
                               {"id":"boot1","title":"Neon","type":"boot_video","likes":1,"downloads":5,"updated_at":"2025-01-01T00:00:00Z"},
                               {"id":"new1","title":"Aurora","type":"boot_video","likes":9,"downloads":1,"updated_at":"2025-06-01T00:00:00Z"},
                               {"id":"s2","title":"Calm night","type":"suspend_video","likes":3,"downloads":7,"updated_at":"2024-01-01T00:00:00Z"}
                             ]}
                             """;
        using var service = Service(answer: new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(posts) });
        service.Start();
        TaskCompletionSource fetched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            if (!service.ReadState().Browse.Loading && service.ReadState().Browse.Total > 0)
            {
                fetched.TrySetResult();
            }
        };

        await service.BrowseAsync("all", "Newest", "", CancellationToken.None);
        await fetched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var browse = service.ReadState().Browse;
        Assert.Equal(3, browse.Total);
        Assert.Equal(["Aurora", "Neon", "Calm night"], browse.Items.Select(item => item.Name));
        Assert.True(browse.Items[1].Downloaded, "the library's copy is marked");
        Assert.Equal("2025-06-01", browse.Items[0].Updated);

        await service.BrowseAsync("boot", "Most popular", "", CancellationToken.None);
        Assert.Equal(["Neon", "Aurora"], service.ReadState().Browse.Items.Select(item => item.Name));
        await service.BrowseAsync("all", "Alphabetical", "cal", CancellationToken.None);
        Assert.Equal(["Calm night"], service.ReadState().Browse.Items.Select(item => item.Name));

        await service.OpenAsync("s2", CancellationToken.None);
        Assert.Equal("Calm night", service.ReadState().Detail!.Name);
        Assert.False(service.ReadState().Detail!.Downloaded);
    }

    [Fact]
    public async Task TheQuickAccessSectionOffersOneChoicePerSlotAndMapsANameBackToItsId()
    {
        using var service = Service();
        service.Start();

        var item = service.ReadExtensionsItem();

        Assert.Equal("wsgm.animations", item.Id);
        Assert.Equal(["Browse animations…", "Library…", "Shuffle"], item.Actions!.Select(action => action.Label));
        var settings = item.Settings!;
        Assert.Equal(("slot:boot", "Steam's own"), (settings[0].Key, settings[0].TextValue));
        Assert.Equal(["Steam's own", "Neon"], settings[0].Choices);
        Assert.Equal(["Steam's own", "Calm"], settings[1].Choices);
        Assert.Equal(["Steam's own", "Calm"], settings[2].Choices);
        Assert.Equal(("shuffleOnStart", false), (settings[3].Key, settings[3].BooleanValue));

        var chosen = await service.ConfigureExtensionAsync("slot:suspend",
            JsonSerializer.SerializeToElement("Calm"), CancellationToken.None);
        Assert.True(chosen.Succeeded);
        Assert.Equal("sus1", _config.Suspend);
        Assert.Equal("Calm", service.ReadExtensionsItem().Settings![1].TextValue);
        var stale = await service.ConfigureExtensionAsync("slot:suspend",
            JsonSerializer.SerializeToElement("Gone"), CancellationToken.None);
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
        Assert.Equal("boot1", _config.Boot);
        Assert.Equal("sus1", _config.Suspend);
        Assert.True(_config.ShuffleOnStart);
        Assert.True(service.ReadState().Settings.ShuffleOnStart);
    }
}
