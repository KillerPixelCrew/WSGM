using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Ir;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.Tests.Builders;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class PluginLoaderTests
{
    [Fact]
    public async Task RealIrPackageLoadsAndPersistsLibraryWithoutHardware()
    {
        using TemporaryDirectory temporary = new();
        var path = WritePackage(temporary.GetPath("ir.wsgmpkg"), $$"""
                                                                   {"id":"wsgm.ir","name":"IR Blaster","version":"0.1.0","category":"wsgm.infrared",
                                                                    "entryAssembly":"WSGM.Plugin.Ir.dll","entryType":"WSGM.Plugin.Ir.IrPlugin","wsgmVersion":"{{PluginPackageFile.HostVersion.ToString(3)}}"}
                                                                   """, typeof(IrPlugin).Assembly.Location,
            "WSGM.Plugin.Ir.dll");
        var manifest = Assert.Single(PluginPackageCatalog.Discover(temporary.Root).Common).Manifest;
        var package = await PluginLoader.LoadCommonAsync(path, manifest, CancellationToken.None);
        PluginHost host = new(action => action(), new MemoryPluginConfigurationStore());
        var irState = temporary.GetPath("ir-state");
        Directory.CreateDirectory(irState);
        var ir = host.Admit(package.Plugin, new PluginInstanceIdentity(package.Plugin.Id, "one"), manifest.Category,
            PluginCategoryPolicy.Multiple, false, 1, irState);
        var deadline = Deadline.After(TimeSpan.FromSeconds(10));
        await ir.StartAsync(deadline, CancellationToken.None);
        Assert.Single(host.Snapshot());
        Assert.Contains(ir.Actions!.Actions, action => action.Id == "save-scene");
        var backup = await host.InvokeActionAsync(ir.Identity, 1, "export", new Dictionary<string, PluginValue>(),
            PluginActionOrigin.User, deadline, CancellationToken.None);
        Assert.Equal(PluginActionOutcome.AppliedVerified, backup.Outcome);
        Assert.True(File.Exists(Path.Combine(irState, "library.backup.json")));
        await host.SetModeAsync(PluginSessionMode.Game, deadline, CancellationToken.None);
        Assert.True(await ir.StopAsync(deadline, CancellationToken.None));
        await ir.DisposeAsync();
        package.Unload();
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
                                                                         "entryAssembly":"{{name}}","entryType":"WSGM.Tests.Fakes.CommonPluginFixture","wsgmVersion":"{{PluginPackageFile.HostVersion.ToString(3)}}"}
                                                                        """, assembly, name);
        var manifest = Assert.Single(PluginPackageCatalog.Discover(temporary.Root).Common).Manifest;
        var package = await PluginLoader.LoadCommonAsync(path, manifest, CancellationToken.None);
        PluginHost host = new(action => action(), new MemoryPluginConfigurationStore());
        var state = temporary.GetPath("state");
        Directory.CreateDirectory(state);
        var registration = host.Admit(package.Plugin, new PluginInstanceIdentity(package.Plugin.Id, "one"),
            manifest.Category, PluginCategoryPolicy.Multiple, false, 1, state);
        var deadline = Deadline.After(TimeSpan.FromSeconds(5));
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
        package.Unload();
        Assert.Empty(host.Snapshot());
        Assert.True(File.Exists(Path.Combine(state, "disposed.txt")));
    }

    [Fact]
    public void CommonPackageAdmissionRejectsTheDeviceCategory()
    {
        using TemporaryDirectory temporary = new();
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
        return PluginPackageBuilders.Write(path, manifest, (entryName, File.ReadAllBytes(assembly)));
    }
}
