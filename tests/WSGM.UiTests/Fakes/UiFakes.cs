using System.ComponentModel;
using WSGM.Interop;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.UiTests.Fakes;

internal sealed class FakePower : IPowerSchemeApi
{
    private static readonly Guid First = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = new("00000000-0000-0000-0000-000000000002");
    private Guid _active = First;
    internal int Writes { get; private set; }
    internal bool Reject { get; set; }
    internal Action? BeforeWrite { get; set; }

    public Guid? Enumerate(uint index)
    {
        return index switch { 0 => First, 1 => Second, _ => null };
    }

    public string ReadName(Guid id)
    {
        return id == First ? "Balanced" : "Power saver";
    }

    public Guid ReadActive()
    {
        return _active;
    }

    public void SetActive(Guid id)
    {
        BeforeWrite?.Invoke();
        Writes++;
        if (Reject)
        {
            throw new Win32Exception(5);
        }

        _active = id;
    }
}

internal sealed class ReadOnlyPowerModeApi : IPowerModeApi
{
    public Guid Read()
    {
        return Guid.Empty;
    }

    public void Set(Guid mode)
    {
        throw new InvalidOperationException("Unexpected Windows power-mode write");
    }
}

internal sealed class UnusedPowerModeApi : IPowerModeApi
{
    public Guid Read()
    {
        throw new InvalidOperationException("Unexpected Windows power-mode read");
    }

    public void Set(Guid mode)
    {
        throw new InvalidOperationException("Unexpected Windows power-mode write");
    }
}

internal sealed class MutableProvider : ICommonPluginOverlaySource
{
    private static readonly PluginInstanceIdentity Identity = new("test", "default");
    public long Generation { get; set; } = 1;
    public bool Present { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool Available { get; set; } = true;
    public int Invocations { get; private set; }

    public PluginOverlayInstance[] Snapshot()
    {
        return !Present
            ? []
            :
            [
                new PluginOverlayInstance(Identity, "Test", Generation, new PluginOverlayControls(
                    [new PluginAction("run", "Run", [])],
                    [new PluginUiContribution("run", "Run", "power", PluginUiKind.Action, ActionId: "run")],
                    [
                        new PluginWidget("power", "Power", ["run"], VisibleStateKey: "available",
                            EnabledStateKey: "enabled")
                    ]), "Ready", true, null)
            ];
    }

    public PluginStatePublication[] State(PluginInstanceIdentity identity)
    {
        return
        [
            new PluginStatePublication(Identity, Generation, 1, "enabled", new PluginValue(Enabled),
                PluginStateOrigin.HardwareReadback),
            new PluginStatePublication(Identity, Generation, 1, "available", new PluginValue(Available),
                PluginStateOrigin.HardwareReadback)
        ];
    }

    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
    {
        Invocations++;
        return Task.FromResult(new PluginActionResult(Guid.NewGuid(), PluginActionOutcome.AppliedVerified));
    }
}

internal sealed class MissingProvider : ICommonPluginOverlaySource
{
    public PluginOverlayInstance[] Snapshot()
    {
        return [];
    }

    public PluginStatePublication[] State(PluginInstanceIdentity identity)
    {
        return [];
    }

    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
        string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("A missing widget cannot dispatch.");
    }
}

internal sealed class ChoiceSource : ICommonPluginOverlaySource
{
    private static readonly PluginInstanceIdentity Identity = new("test", "device");
    public string? Requested { get; private set; }

    public PluginOverlayInstance[] Snapshot()
    {
        return
        [
            new PluginOverlayInstance(Identity, "Test", 1, new PluginOverlayControls(
                [
                    new PluginAction("fan", "Change fan",
                    [
                        new PluginSetting("value", "Fan", PluginSettingKind.Text, new PluginValue(Text: "quiet"),
                            Choices: ["quiet", "turbo"])
                    ])
                ],
                [new PluginUiContribution("edit", "Change fan", "device", PluginUiKind.Action, ActionId: "fan")],
                [new PluginWidget("fan", "Fan", ["edit"])]), "Ready", true, null)
        ];
    }

    public PluginStatePublication[] State(PluginInstanceIdentity identity)
    {
        return [];
    }

    public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation, string action,
        IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
    {
        Requested = arguments["value"].Text;
        return Task.FromResult(new PluginActionResult(Guid.NewGuid(), PluginActionOutcome.AppliedVerified));
    }
}
