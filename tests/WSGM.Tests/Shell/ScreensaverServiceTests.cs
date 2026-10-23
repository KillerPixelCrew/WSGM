using SteamUiToolkit;
using WSGM.Core;
using WSGM.Shell;
using WSGM.Testing;

namespace WSGM.Tests.Shell;

public sealed class ScreensaverServiceTests
{
    [Fact]
    public async Task CoversAreBoundedLazyAndAddressOnlyRegisteredSources()
    {
        using var files = new TemporaryDirectory();
        var folder = Path.Combine(files.Root, "photos");
        Directory.CreateDirectory(folder);
        WriteBmp(Path.Combine(folder, "cover.bmp"), 100);
        var config = Configuration(folder);
        await using var service = Service(files.Root, config);
        var before = service.ReadState();
        Assert.True(before.Sources[0].HasPreview);
        Assert.Equal(1, before.Sources[0].ImageCount);
        var cover = await service.ReadThumbnailAsync("photos", CancellationToken.None);
        Assert.NotNull(cover);
        Assert.InRange(cover.Length, 1, 64 * 1024);
        Assert.Equal(0xff, cover[0]);
        Assert.Equal(0xd8, cover[1]);
        Assert.Same(cover, await service.ReadThumbnailAsync("photos", CancellationToken.None));
        Assert.Null(await service.ReadThumbnailAsync("../unregistered.bmp", CancellationToken.None));
        Assert.Equal(before.Revision, service.Snapshot().Revision);
        Assert.DoesNotContain("data:image", SteamScreensaverSurface.Serialize(new SteamScreensaverState([], 0)
        {
            Content = service.Snapshot()
        }).ToString(), StringComparison.Ordinal);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadThumbnailAsync("photos", cancelled.Token));
    }

    [Fact]
    public async Task RenameKeepsSourceIdentityAndOptionsAndRemapsTheNativeSelection()
    {
        using var files = new TemporaryDirectory();
        WriteBmp(Path.Combine(files.Root, "cover.bmp"), 100);
        var config = Configuration(files.Root);
        config.Sources.Add(new ScreensaverSourceConfig { Id = "other", Name = "Other", Folder = files.Root });
        await using var service = Service(files.Root, config);
        service.ReadState();
        await service.ExecuteAsync(new SteamScreensaverContentCommand
        {
            Action = "playback", Id = "photos", SelectorId = ScreensaverContent.SelectorId("Photos"), Active = false
        }, CancellationToken.None);
        var result = await service.ExecuteAsync(new SteamScreensaverContentCommand
        {
            Action = "rename", Id = "photos", Name = "Travel photos"
        }, CancellationToken.None);
        Assert.True(result.Succeeded);
        var source = config.Sources[0];
        Assert.Equal("photos", source.Id);
        Assert.Equal("Travel photos", source.Name);
        Assert.False(source.Shuffle);
        Assert.True(source.Fill);
        Assert.Equal(17, source.IntervalSeconds);
        Assert.Equal(ScreensaverContent.SelectorId("Travel photos"), service.Snapshot().SelectionRequest);
        Assert.False(service.Snapshot().PreviewSelection);
        var duplicate = await service.ExecuteAsync(new SteamScreensaverContentCommand
        {
            Action = "rename", Id = "photos", Name = "Other"
        }, CancellationToken.None);
        Assert.False(duplicate.Succeeded);
        Assert.Equal("Travel photos", source.Name);
    }

    [Fact]
    public async Task ChangingTheFolderRefreshesTheCoverWithoutReplacingOtherPreferences()
    {
        using var files = new TemporaryDirectory();
        var first = Path.Combine(files.Root, "first");
        var second = Path.Combine(files.Root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        WriteBmp(Path.Combine(first, "cover.bmp"), 30);
        WriteBmp(Path.Combine(second, "cover.bmp"), 160);
        var config = Configuration(first);
        await using var service = Service(files.Root, config);
        service.ReadState();
        var before = await service.ReadThumbnailAsync("photos", CancellationToken.None);
        var result = await service.ExecuteAsync(new SteamScreensaverContentCommand
        {
            Action = "relocate", Id = "photos", Folder = second
        }, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(second, config.Sources[0].Folder);
        Assert.Equal(17, config.Sources[0].IntervalSeconds);
        Assert.False(config.Sources[0].Shuffle);
        Assert.True(config.Sources[0].Fill);
        Assert.NotEqual(before, await service.ReadThumbnailAsync("photos", CancellationToken.None));
    }

    private static ScreensaverConfig Configuration(string folder)
    {
        return new ScreensaverConfig
        {
            Sources = [new ScreensaverSourceConfig
            {
                Id = "photos", Name = "Photos", Folder = folder, IntervalSeconds = 17, Fill = true, Shuffle = false
            }]
        };
    }

    private static ScreensaverService Service(string root, ScreensaverConfig config)
    {
        return new ScreensaverService(new UserDataContext(root, "Local\\WSGM.Tests." + Guid.NewGuid().ToString("N")),
            () => config, change => change(config), () => true);
    }

    private static void WriteBmp(string path, byte color)
    {
        const int width = 320;
        const int height = 200;
        const int pixels = width * height * 3;
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write((ushort)0x4d42);
        writer.Write(54 + pixels);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)24);
        writer.Write(0);
        writer.Write(pixels);
        writer.Write(new byte[16]);
        for (var index = 0; index < width * height; index++)
        {
            writer.Write(color);
            writer.Write((byte)(color / 2));
            writer.Write((byte)(255 - color));
        }
    }
}
