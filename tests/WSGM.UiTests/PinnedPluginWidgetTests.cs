using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class PinnedPluginWidgetTests
{
    [AvaloniaFact]
    public void ReloadRejectsOldControlAndRecoversSamePin()
    {
        using UiFixture fixture = new();
        MutableProvider source = new();
        CommonPluginPanel panel = new(source, new PluginWidgetPin("test", "default", "power"));
        Window window = new() { Content = panel, Width = 500, Height = 400 };
        try
        {
            window.Show();
            var old = panel.GetLogicalDescendants().OfType<Button>().Single();
            source.Generation++;
            UiFixture.Click(window, old);
            Assert.Equal(0, source.Invocations);
            source.Present = false;
            panel.Refresh();
            Assert.Equal("Plugin unavailable", Assert.IsType<TextBlock>(Assert.Single(panel.Children)).Text);
            source.Present = true;
            panel.Refresh();
            var recovered = panel.GetLogicalDescendants().OfType<Button>().Single();
            Assert.NotSame(old, recovered);
            UiFixture.Click(window, recovered);
            Assert.Equal(1, source.Invocations);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PredicateChangeBeforeRefreshPreventsDispatch()
    {
        using UiFixture fixture = new();
        MutableProvider source = new();
        CommonPluginPanel panel = new(source, new PluginWidgetPin("test", "default", "power"));
        Window window = new() { Content = panel, Width = 500, Height = 400 };
        try
        {
            window.Show();
            source.Enabled = false;
            UiFixture.Click(window, panel.GetLogicalDescendants().OfType<Button>().Single());
            Assert.Equal(0, source.Invocations);
            panel.Refresh();
            Assert.False(panel.GetLogicalDescendants().OfType<Button>().Single().IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    private sealed class MutableProvider : ICommonPluginOverlaySource
    {
        private static readonly PluginInstanceIdentity Identity = new("test", "default");
        public long Generation { get; set; } = 1;
        public bool Present { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public int Invocations { get; private set; }
        public PluginOverlayInstance[] Snapshot() => !Present ? [] :
        [new(Identity, "Test", Generation, new([new("run", "Run", [])],
            [new("run", "Run", "power", PluginUiKind.Action, ActionId: "run")],
            [new("power", "Power", ["run"], EnabledStateKey: "enabled")]), "Ready", true, null)];
        public PluginStatePublication[] State(PluginInstanceIdentity identity) =>
            [new(Identity, Generation, 1, "enabled", new(Boolean: Enabled), PluginStateOrigin.HardwareReadback)];
        public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
            string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(new PluginActionResult(Guid.NewGuid(), PluginActionOutcome.AppliedVerified));
        }
    }

    [AvaloniaFact]
    public void MissingPinnedProviderRemainsVisibleWithoutDispatching()
    {
        using UiFixture fixture = new();
        CommonPluginPanel panel = new(new MissingProvider(), new PluginWidgetPin("missing", "default", "power"));
        Window window = new() { Content = panel, Width = 500, Height = 200 };
        try
        {
            window.Show();
            Assert.True(panel.IsVisible);
            Assert.Equal("Plugin unavailable", Assert.IsType<TextBlock>(Assert.Single(panel.Children)).Text);
        }
        finally { window.Close(); }
    }

    private sealed class MissingProvider : ICommonPluginOverlaySource
    {
        public PluginOverlayInstance[] Snapshot() => [];
        public PluginStatePublication[] State(PluginInstanceIdentity identity) => [];
        public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation,
            string action, IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A missing widget cannot dispatch.");
    }
}
