using System.Text.Json;
using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core;

public sealed class EmulatorBiosTests
{
    [Fact]
    public async Task FolderAndVerificationPersistWithoutChangingEmulatorPreferences()
    {
        using var temporary = new TemporaryDirectory();
        var bios = Directory.CreateDirectory(Path.Combine(temporary.Root, "bios")).FullName;
        var preference = new EmulatorSystemPreference { SystemId = "psx", InstallationId = "external" };
        File.WriteAllText(EmulatorStorage.StorePath(temporary.Root),
            JsonSerializer.Serialize(new EmulatorStore { SystemPreferences = [preference] },
                EmulatorStorage.JsonOptions));
        File.WriteAllText(Path.Combine(bios, "scph5501.bin"), "incorrect BIOS bytes");
        using var manager = new EmulatorManager(new UserDataContext(temporary.Root, "unused-test-config"));
        await manager.Initialization;
        await manager.SetBiosFolderAsync(bios, CancellationToken.None);
        var system = Assert.Single(manager.ReadBiosState().Systems, item => item.Id == "psx");
        Assert.Equal("Wrong file", Assert.Single(system.Files, item => item.Path == "scph5501.bin").Status);
        Assert.Equal("Missing", Assert.Single(system.Files, item => item.Path == "scph5500.bin").Status);
        var store = EmulatorStorage.ReadStore(temporary.Root);
        Assert.Equal(bios, store.BiosFolder);
        Assert.Equal(preference, Assert.Single(store.SystemPreferences));
    }

    [Fact]
    public async Task UnreadableStoreRefusesFolderChangeAndPreservesOriginalBytes()
    {
        using var temporary = new TemporaryDirectory();
        var bios = Directory.CreateDirectory(Path.Combine(temporary.Root, "bios")).FullName;
        var path = EmulatorStorage.StorePath(temporary.Root);
        File.WriteAllText(path, "unreadable");
        using var manager = new EmulatorManager(new UserDataContext(temporary.Root, "unused-test-config"));
        await manager.Initialization;
        await Assert.ThrowsAsync<JsonException>(() => manager.SetBiosFolderAsync(bios, CancellationToken.None));
        Assert.Equal("unreadable", File.ReadAllText(path));
    }

    [Fact]
    public async Task ImportRecognizesEmuDeckSubfoldersAndDoesNotDeleteTheSource()
    {
        using var temporary = new TemporaryDirectory();
        var bios = Directory.CreateDirectory(Path.Combine(temporary.Root, "bios")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(temporary.Root, "dump", "dc")).FullName;
        var file = Path.Combine(source, "dc_flash.bin");
        File.WriteAllText(file, "incorrect local dump");
        using var manager = new EmulatorManager(new UserDataContext(temporary.Root, "unused-test-config"));
        await manager.Initialization;
        await manager.SetBiosFolderAsync(bios, CancellationToken.None);
        await manager.AddBiosFilesAsync(Path.GetDirectoryName(source)!, "", CancellationToken.None);
        Assert.Equal(File.ReadAllText(file), File.ReadAllText(Path.Combine(bios, "dc", "dc_flash.bin")));
        Assert.Equal("Wrong file", manager.ReadBiosState().Systems.Single(item => item.Id == "dreamcast")
            .Files.Single(item => item.Path == "dc/dc_flash.bin").Status);
    }
}
