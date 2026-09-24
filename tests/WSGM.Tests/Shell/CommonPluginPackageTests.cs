using System.IO.Compression;
using System.Text;
using WSGM.Core;
using WSGM.Device.Tests;
using WSGM.Plugin.Ir;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class CommonPluginPackageTests
{
    [Fact]
    public async Task RealIrPackageLoadsAlongsideDeviceCategoryAndPersistsLibraryWithoutHardware()
    {
        using TemporaryDirectory temporary = new();
        var path = WritePackage(temporary.GetPath("ir.wsgmpkg"), """
                                                                 {"id":"wsgm.ir","name":"IR Blaster","version":"0.1.0","category":"wsgm.infrared",
                                                                  "entryAssembly":"WSGM.Plugin.Ir.dll","entryType":"WSGM.Plugin.Ir.IrPlugin"}
                                                                 """, typeof(IrPlugin).Assembly.Location,
            "WSGM.Plugin.Ir.dll");
        var manifest = Assert.Single(PluginPackageCatalog.Discover(temporary.Root).Common).Manifest;
        var package = await CommonPluginPackage.LoadAsync(path, manifest, CancellationToken.None);
        PluginHost host = new(action => action(), new MemoryPluginConfigurationStore());
        var deviceState = temporary.GetPath("device-state");
        var irState = temporary.GetPath("ir-state");
        Directory.CreateDirectory(deviceState);
        Directory.CreateDirectory(irState);
        var device = host.Admit(new CommonPluginFixture(), new PluginInstanceIdentity("test.common-fixture", "device"),
            PluginCategories.Device, PluginCategoryPolicy.Device, true, 1, deviceState);
        var ir = host.Admit(package, new PluginInstanceIdentity(package.Id, "one"), manifest.Category,
            PluginCategoryPolicy.Multiple, false, 1, irState);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        await device.StartAsync(deadline, CancellationToken.None);
        await ir.StartAsync(deadline, CancellationToken.None);
        Assert.Equal(2, host.Snapshot().Length);
        Assert.Contains(ir.Actions!.Actions, action => action.Id == "save-scene");
        var backup = await host.InvokeActionAsync(ir.Identity, 1, "export", new Dictionary<string, PluginValue>(),
            PluginActionOrigin.User, deadline, CancellationToken.None);
        Assert.Equal(PluginActionOutcome.AppliedVerified, backup.Outcome);
        Assert.True(File.Exists(Path.Combine(irState, "library.backup.json")));
        await host.SetModeAsync(PluginSessionMode.Game, deadline, CancellationToken.None);
        Assert.True(await ir.StopAsync(deadline, CancellationToken.None));
        await ir.DisposeAsync();
        Assert.Single(host.Snapshot());
        Assert.True(await device.StopAsync(deadline, CancellationToken.None));
        await device.DisposeAsync();
        Assert.Empty(host.Snapshot());
    }

    [Fact]
    public async Task ACollectibleNonDevicePackageRunsConfigurationActionsAndResidentTransitions()
    {
        using TemporaryDirectory temporary = new();
        var assembly = typeof(CommonPluginFixture).Assembly.Location;
        var name = Path.GetFileName(assembly);
        var path = WritePackage(temporary.GetPath("fixture.wsgmpkg"), $$"""
                                                                        {"id":"test.common-fixture","name":"Fixture","version":"1.0.0","category":"example.status",
                                                                         "entryAssembly":"{{name}}","entryType":"WSGM.Tests.Fakes.CommonPluginFixture"}
                                                                        """, assembly, name);
        var manifest = Assert.Single(PluginPackageCatalog.Discover(temporary.Root).Common).Manifest;
        var package = await CommonPluginPackage.LoadAsync(path, manifest, CancellationToken.None);
        PluginHost host = new(action => action(), new MemoryPluginConfigurationStore());
        var state = temporary.GetPath("state");
        Directory.CreateDirectory(state);
        var registration = host.Admit(package, new PluginInstanceIdentity(package.Id, "one"), manifest.Category,
            PluginCategoryPolicy.Multiple, false, 1, state);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        await registration.StartAsync(deadline, CancellationToken.None);
        Assert.True(host.StateSnapshot(registration.Identity).Single(value => value.Key == "collectible").Value
            .Boolean);
        await registration.ConfigureAsync(0, new Dictionary<string, PluginValue> { ["label"] = new(Text: "hello") },
            deadline, CancellationToken.None);
        await host.SetModeAsync(PluginSessionMode.Game, deadline, CancellationToken.None);
        var result = await host.InvokeActionAsync(registration.Identity, 1, "record",
            new Dictionary<string, PluginValue>(),
            PluginActionOrigin.SessionAutomation, deadline, CancellationToken.None);
        Assert.Equal(PluginActionOutcome.AppliedVerified, result.Outcome);
        Assert.Equal("hello:Game", await File.ReadAllTextAsync(Path.Combine(state, "action.txt")));
        Assert.Single(registration.Actions!.Contributions);
        Assert.True(await registration.StopAsync(deadline, CancellationToken.None));
        await registration.DisposeAsync();
        Assert.Empty(host.Snapshot());
        Assert.True(File.Exists(Path.Combine(state, "disposed.txt")));
    }

    [Fact]
    public void CommonPackageAdmissionRejectsOversizedMetadataAndDeviceCategory()
    {
        using TemporaryDirectory temporary = new();
        var oversized = WritePackage(temporary.GetPath("oversized.wsgmpkg"),
            new string(' ', PluginManifestReader.MaximumBytes + 1) + "{}",
            typeof(CommonPluginFixture).Assembly.Location,
            "Fixture.dll");
        Assert.Throws<InvalidDataException>(() => PluginPackageFile.Open(oversized).Dispose());
        var device = WritePackage(temporary.GetPath("device.wsgmpkg"), """
                                                                       {"id":"test.fixture","name":"Fixture","version":"1.0","category":"wsgm.device",
                                                                        "entryAssembly":"Fixture.dll","entryType":"Fixture.Plugin"}
                                                                       """,
            typeof(CommonPluginFixture).Assembly.Location,
            "Fixture.dll");
        Assert.Throws<InvalidDataException>(() => PluginPackageFile.Open(device).Dispose());
    }

    private static string WritePackage(string path, string manifest, string assembly, string entryName)
    {
        using var stream = File.Create(path);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        using (var entry = archive.CreateEntry("plugin.wsgm.json").Open())
        {
            entry.Write(Encoding.UTF8.GetBytes(manifest));
        }

        using (var entry = archive.CreateEntry(entryName).Open())
        {
            entry.Write(File.ReadAllBytes(assembly));
        }

        return path;
    }
}
