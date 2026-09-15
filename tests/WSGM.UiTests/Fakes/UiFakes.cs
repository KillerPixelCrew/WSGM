using System.ComponentModel;
using WSGM.Interop;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.UiTests;

internal sealed class FakePower : IPowerSchemeApi
{
    private static readonly Guid First = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = new("00000000-0000-0000-0000-000000000002");
    private Guid _active = First;
    internal int Writes { get; private set; }
    internal bool Reject { get; set; }
    internal Action? BeforeWrite { get; set; }
    public Guid? Enumerate(uint index) => index switch { 0 => First, 1 => Second, _ => null };
    public string ReadName(Guid id) => id == First ? "Balanced" : "Power saver";
    public Guid ReadActive() => _active;
    public void SetActive(Guid id)
    {
        BeforeWrite?.Invoke();
        Writes++;
        if (Reject) { throw new Win32Exception(5); }
        _active = id;
    }
}

internal sealed class ReadOnlyPowerModeApi : IPowerModeApi
{
    public Guid Read() => Guid.Empty;
    public void Set(Guid mode) => throw new InvalidOperationException("Unexpected Windows power-mode write");
}

internal sealed class UnusedPowerModeApi : IPowerModeApi
{
    public Guid Read() => throw new InvalidOperationException("Unexpected Windows power-mode read");
    public void Set(Guid mode) => throw new InvalidOperationException("Unexpected Windows power-mode write");
}

internal sealed class MutableProvider : ICommonPluginOverlaySource
{
    private static readonly PluginInstanceIdentity Identity = new("test", "default");
    public long Generation { get; set; } = 1;
    public bool Present { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool Available { get; set; } = true;
    public int Invocations { get; private set; }
    public PluginOverlayInstance[] Snapshot() => !Present ? [] :
    [new(Identity, "Test", Generation, new([new("run", "Run", [])],
        [new("run", "Run", "power", PluginUiKind.Action, ActionId: "run")],
        [new("power", "Power", ["run"], VisibleStateKey: "available", EnabledStateKey: "enabled")]), "Ready", true, null)];
    public PluginStatePublication[] State(PluginInstanceIdentity identity) =>
        [new(Identity, Generation, 1, "enabled", new(Boolean: Enabled), PluginStateOrigin.HardwareReadback),
            new(Identity, Generation, 1, "available", new(Boolean: Available), PluginStateOrigin.HardwareReadback)];
    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
    {
        Invocations++;
        return Task.FromResult(new PluginActionResult(Guid.NewGuid(), PluginActionOutcome.AppliedVerified));
    }
}

internal sealed class MissingProvider : ICommonPluginOverlaySource
{
    public PluginOverlayInstance[] Snapshot() => [];
    public PluginStatePublication[] State(PluginInstanceIdentity identity) => [];
    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A missing widget cannot dispatch.");
}

internal sealed class ChoiceSource : ICommonPluginOverlaySource
{
    private static readonly PluginInstanceIdentity Identity = new("test", "device");
    public string? Requested { get; private set; }
    public PluginOverlayInstance[] Snapshot() =>
    [new(Identity, "Test", 1, new(
        [new("fan", "Change fan", [new("value", "Fan", PluginSettingKind.Text, new(Text: "quiet"), Choices: ["quiet", "turbo"])])],
        [new("edit", "Change fan", "device", PluginUiKind.Action, ActionId: "fan")],
        [new("fan", "Fan", ["edit"])]), "Ready", true, null)];
    public PluginStatePublication[] State(PluginInstanceIdentity identity) => [];
    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation, string action,
        IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
    {
        Requested = arguments["value"].Text;
        return Task.FromResult(new PluginActionResult(Guid.NewGuid(), PluginActionOutcome.AppliedVerified));
    }
}
