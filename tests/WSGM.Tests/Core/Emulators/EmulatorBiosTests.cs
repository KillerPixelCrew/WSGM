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
    [Fact]
    public async Task SelectingAndCheckingFolderLinksRetroArchAndPreservesItsExistingFiles()
    {
        using var temporary = new TemporaryDirectory();
        var bios = Directory.CreateDirectory(Path.Combine(temporary.Root, "bios")).FullName;
        var secondBios = Directory.CreateDirectory(Path.Combine(temporary.Root, "second-bios")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(temporary.Root, "retroarch")).FullName;
        var system = Directory.CreateDirectory(Path.Combine(data, "system")).FullName;
        File.WriteAllText(Path.Combine(system, "local-core-file.bin"), "keep this file");
        File.WriteAllText(Path.Combine(bios, "FMT_DIC.ROM"), "supplied core file");
        var installed = new EmulatorInstallation
        {
            Cores = [new EmulatorCore { RequiredFiles = [Path.Combine(system, "geartowns", "FMT_DIC.ROM")] }],
            Id = "retroarch-test", DefinitionId = "retroarch", Name = "RetroArch", DataPath = data,
            DataPolicy = new EmulatorDataPolicy
            {
                HasCores = true, ConfigFile = "retroarch.cfg",
                Prerequisites = [new EmulatorPrerequisite { Kind = "system", Destination = "system" }]
            }
        };
        File.WriteAllText(EmulatorStorage.StorePath(temporary.Root),
            JsonSerializer.Serialize(new EmulatorStore { Installations = [installed] }, EmulatorStorage.JsonOptions));
        using var manager = new EmulatorManager(new UserDataContext(temporary.Root, "unused-test-config"));
        await manager.Initialization;
        await manager.SetBiosFolderAsync(bios, CancellationToken.None);
        Assert.Equal(bios, new DirectoryInfo(system).ResolveLinkTarget(true)!.FullName);
        Assert.Equal("keep this file", File.ReadAllText(Path.Combine(bios, "local-core-file.bin")));
        Assert.Equal("supplied core file", File.ReadAllText(Path.Combine(system, "geartowns", "FMT_DIC.ROM")));
        Assert.Equal(bios.Replace('\\', '/'), IniFile.ReadValue(Path.Combine(data, "retroarch.cfg"),
            "system_directory")!.Trim('"'));

        await manager.SetBiosFolderAsync(secondBios, CancellationToken.None);
        await manager.VerifyBiosAsync(CancellationToken.None);
        Assert.Equal(secondBios, new DirectoryInfo(system).ResolveLinkTarget(true)!.FullName);
        Assert.Equal("keep this file", File.ReadAllText(Path.Combine(bios, "local-core-file.bin")));
        Assert.Equal(secondBios, EmulatorStorage.ReadInstallations(temporary.Root).Single()
            .ConfiguredPrerequisites["system"]);
    }
    [Fact]
    public async Task ScanFindsVersionedSwitchFirmwareArchiveBesideKeys()
    {
        using var temporary = new TemporaryDirectory();
        var bios = Directory.CreateDirectory(Path.Combine(temporary.Root, "bios")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(bios, "SWITCH")).FullName;
        var path = Path.Combine(folder, "Firmware 23.0.1.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("0123456789abcdef0123456789abcdef.nca").Open();
            stream.WriteByte(1);
        }

        using var manager = new EmulatorManager(new UserDataContext(temporary.Root, "unused-test-config"));
        await manager.Initialization;
        await manager.SetBiosFolderAsync(bios, CancellationToken.None);
        var firmware = manager.ReadBiosState().Systems.Single(item => item.Id == "switch").Files
            .Single(item => item.Path.EndsWith(".zip", StringComparison.Ordinal));
        Assert.Equal("Present", firmware.Status);
        Assert.Equal("SWITCH/Firmware 23.0.1.zip", firmware.Path, ignoreCase: true);
        Assert.True(File.Exists(path));
    }
}
