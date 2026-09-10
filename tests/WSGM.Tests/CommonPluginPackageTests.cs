using System.Runtime.Loader;
using WSGM.Core;
using WSGM.Device.Tests;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class CommonPluginPackageTests
{
    [Fact]
    public async Task RealIrPackageLoadsAlongsideDeviceCategoryAndPersistsLibraryWithoutHardware()
    {
        using TemporaryDirectory temporary = new();
        string root = temporary.GetPath("ir-package");
        Directory.CreateDirectory(root);
        string assembly = typeof(WSGM.Plugin.Ir.IrPlugin).Assembly.Location;
        File.Copy(assembly, Path.Combine(root, "WSGM.Plugin.Ir.dll"));
        await File.WriteAllTextAsync(Path.Combine(root, "plugin.wsgm.json"), """
            {"id":"wsgm.ir","name":"IR Blaster","version":"0.1.0","category":"wsgm.infrared",
             "entryAssembly":"WSGM.Plugin.Ir.dll","entryType":"WSGM.Plugin.Ir.IrPlugin"}
            """);
        var manifest = CommonPluginPackage.ReadManifest(root);
        var package = await CommonPluginPackage.LoadAsync(root, manifest, default);
        PluginHost host = new(action => action(), new MemoryStore());
        string deviceState = temporary.GetPath("device-state");
        string irState = temporary.GetPath("ir-state");
        Directory.CreateDirectory(deviceState);
        Directory.CreateDirectory(irState);
        var device = host.Admit(new CommonPluginFixture(), new("test.common-fixture", "device"),
            PluginCategories.Device, PluginCategoryPolicy.Device, true, 1, deviceState);
        var ir = host.Admit(package, new(package.Id, "one"), manifest.Category, PluginCategoryPolicy.Multiple, false, 1, irState);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        await device.StartAsync(deadline, default);
        await ir.StartAsync(deadline, default);
        Assert.Equal(2, host.Snapshot().Length);
        Assert.Contains(ir.Actions!.Actions, action => action.Id == "save-scene");
        var backup = await host.InvokeActionAsync(ir.Identity, 1, "export", new Dictionary<string, PluginValue>(),
            PluginActionOrigin.User, deadline, default);
        Assert.Equal(PluginActionOutcome.AppliedVerified, backup.Outcome);
        Assert.True(File.Exists(Path.Combine(irState, "library.backup.json")));
        await host.SetModeAsync(PluginSessionMode.Game, deadline, default);
        Assert.True(await ir.StopAsync(deadline, default));
        await ir.DisposeAsync();
        Assert.Single(host.Snapshot());
        Assert.True(await device.StopAsync(deadline, default));
        await device.DisposeAsync();
        Assert.Empty(host.Snapshot());
    }

    [Fact]
    public async Task ACollectibleNonDevicePackageRunsConfigurationActionsAndResidentTransitions()
    {
        using TemporaryDirectory temporary = new();
        string root = temporary.GetPath("package");
        Directory.CreateDirectory(root);
        string assembly = typeof(CommonPluginFixture).Assembly.Location;
        string name = Path.GetFileName(assembly);
        File.Copy(assembly, Path.Combine(root, name));
        await File.WriteAllTextAsync(Path.Combine(root, "plugin.wsgm.json"), $$"""
            {"id":"test.common-fixture","name":"Fixture","version":"1.0.0","category":"example.status",
             "entryAssembly":"{{name}}","entryType":"WSGM.Tests.CommonPluginFixture"}
            """);
        var manifest = CommonPluginPackage.ReadManifest(root);
        var package = await CommonPluginPackage.LoadAsync(root, manifest, default);
        PluginHost host = new(action => action(), new MemoryStore());
        string state = temporary.GetPath("state");
        Directory.CreateDirectory(state);
        var registration = host.Admit(package, new(package.Id, "one"), manifest.Category, PluginCategoryPolicy.Multiple, false, 1, state);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        await registration.StartAsync(deadline, default);
        Assert.True(host.StateSnapshot(registration.Identity).Single(value => value.Key == "collectible").Value.Boolean);
        await registration.ConfigureAsync(0, new Dictionary<string, PluginValue> { ["label"] = new(Text: "hello") }, deadline, default);
        await host.SetModeAsync(PluginSessionMode.Game, deadline, default);
        var result = await host.InvokeActionAsync(registration.Identity, 1, "record", new Dictionary<string, PluginValue>(),
            PluginActionOrigin.SessionAutomation, deadline, default);
        Assert.Equal(PluginActionOutcome.AppliedVerified, result.Outcome);
        Assert.Equal("hello:Game", await File.ReadAllTextAsync(Path.Combine(state, "action.txt")));
        Assert.Single(registration.Actions!.Contributions);
        Assert.True(await registration.StopAsync(deadline, default));
        await registration.DisposeAsync();
        Assert.Empty(host.Snapshot());
        Assert.True(File.Exists(Path.Combine(state, "disposed.txt")));
    }

    [Fact]
    public async Task CommonPackageAdmissionRejectsOversizedMetadataAndDeviceCategory()
    {
        using TemporaryDirectory temporary = new();
        string root = temporary.GetPath("package");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "plugin.wsgm.json");
        await File.WriteAllTextAsync(path, new string(' ', PluginManifestReader.MaximumBytes + 1));
        Assert.Throws<InvalidDataException>(() => CommonPluginPackage.ReadManifest(root));
        await File.WriteAllTextAsync(path, """
            {"id":"test.fixture","name":"Fixture","version":"1.0","category":"wsgm.device",
             "entryAssembly":"Fixture.dll","entryType":"Fixture.Plugin"}
            """);
        Assert.Throws<InvalidDataException>(() => CommonPluginPackage.ReadManifest(root));
    }

    private sealed class MemoryStore : IPluginConfigurationStore
    {
        private readonly AppConfig _config = new();
        public SavedPluginConfiguration Read(PluginInstanceIdentity identity) => ApplicationPluginConfigurationStore.ReadFrom(_config, identity);
        public SavedPluginConfiguration Save(PluginInstanceIdentity identity, long revision, IReadOnlyDictionary<string, PluginValue> changes)
        { ApplicationPluginConfigurationStore.SaveInto(_config, identity, revision, changes); return Read(identity); }
    }
}

/// <summary>Hardware-free package fixture using only common SDK and BCL contracts.</summary>
public sealed class CommonPluginFixture : IPlugin, IConfigurablePlugin, IPluginActions, IPluginUi
{
    private string _directory = "";
    private string _label = "";
    private PluginSessionMode _mode;
    public string Id => "test.common-fixture";
    public IReadOnlyList<PluginSetting> Settings => [new("label", "Label", PluginSettingKind.Text, new(Text: "default"))];
    public IReadOnlyList<PluginAction> Actions => [new("record", "Record", [])];
    public IReadOnlyList<PluginUiContribution> Contributions => [new("record", "Record", "fixture", PluginUiKind.Action, ActionId: "record")];
    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken)
    {
        _directory = context.StateDirectory;
        _mode = context.Mode;
        host.PublishState(new(context.Instance, context.Generation, 1, "collectible",
            new(Boolean: AssemblyLoadContext.GetLoadContext(typeof(CommonPluginFixture).Assembly)!.IsCollectible), PluginStateOrigin.Initialization));
        return ValueTask.FromResult(PluginHealth.Ready);
    }
    public ValueTask<PluginConfigurationResult> ConfigureAsync(PluginConfiguration configuration, PluginContext context, CancellationToken cancellationToken)
    { _label = configuration.Values["label"].Text!; return ValueTask.FromResult(new PluginConfigurationResult(configuration.Revision, PluginConfigurationOutcome.Applied)); }
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    { _mode = context.Mode; return ValueTask.CompletedTask; }
    public async ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, PluginContext context, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_directory, "action.txt");
        string expected = _label + ":" + _mode;
        await File.WriteAllTextAsync(path, expected, cancellationToken);
        string actual = await File.ReadAllTextAsync(path, cancellationToken);
        return new(request.OperationId, actual == expected ? PluginActionOutcome.AppliedVerified : PluginActionOutcome.Unconfirmed);
    }
    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    { return ValueTask.FromResult(true); }
    public async ValueTask DisposeAsync()
    {
        if (_directory.Length > 0) { await File.WriteAllTextAsync(Path.Combine(_directory, "disposed.txt"), "released"); }
    }
}
