using System.Runtime.Loader;
using WSGM.Plugin.Sdk;

namespace WSGM.Tests;

/// <summary>Hardware-free package fixture using only common SDK and BCL contracts.</summary>
public sealed class CommonPluginFixture : IPlugin, IConfigurablePlugin, IPluginActions, IPluginUi
{
    private string _directory = "";
    private string _label = "";
    private PluginSessionMode _mode;
    public string Id => "test.common-fixture";
    public IReadOnlyList<PluginSetting> Settings => [new("label", "Label", PluginSettingKind.Text, new PluginValue(Text: "default"))];
    public IReadOnlyList<PluginAction> Actions => [new("record", "Record", [])];
    public IReadOnlyList<PluginUiContribution> Contributions => [new("record", "Record", "fixture", PluginUiKind.Action, ActionId: "record")];
    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken)
    {
        _directory = context.StateDirectory;
        _mode = context.Mode;
        host.PublishState(new PluginStatePublication(context.Instance, context.Generation, 1, "collectible",
            new PluginValue(Boolean: AssemblyLoadContext.GetLoadContext(typeof(CommonPluginFixture).Assembly)!.IsCollectible), PluginStateOrigin.Initialization));
        return ValueTask.FromResult(PluginHealth.Ready);
    }
    public ValueTask<PluginConfigurationResult> ConfigureAsync(PluginConfiguration configuration, PluginContext context, CancellationToken cancellationToken)
    { _label = configuration.Values["label"].Text!; return ValueTask.FromResult(new PluginConfigurationResult(configuration.Revision, PluginConfigurationOutcome.Applied)); }
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    { _mode = context.Mode; return ValueTask.CompletedTask; }
    public async ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, PluginContext context, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, "action.txt");
        var expected = _label + ":" + _mode;
        await File.WriteAllTextAsync(path, expected, cancellationToken);
        var actual = await File.ReadAllTextAsync(path, cancellationToken);
        return new PluginActionResult(request.OperationId, actual == expected ? PluginActionOutcome.AppliedVerified : PluginActionOutcome.Unconfirmed);
    }
    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    { return ValueTask.FromResult(true); }
    public async ValueTask DisposeAsync()
    {
        if (_directory.Length > 0) { await File.WriteAllTextAsync(Path.Combine(_directory, "disposed.txt"), "released"); }
    }
}
