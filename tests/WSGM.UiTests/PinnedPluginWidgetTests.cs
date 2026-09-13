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
    public void PluginPageHasOnePinTogglePerWidgetAndSharedActionCards()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var host = UiFixture.Named<StackPanel>(window, "CommonPluginRows");
        host.Children.Add(new CommonPluginPanel(new MutableProvider(), readPins: () => Task.FromResult(Array.Empty<PluginWidgetPin>())));
        var tile = UiFixture.Named<WSGM.Controls.CardButton>(window, "SystemPluginsTile");
        tile.IsVisible = true;
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, tile);
        Assert.Single(host.GetLogicalDescendants().OfType<PluginWidgetPinControls>());
        VisualBaseline.Verify(window, "overlay-plugins-1280");
    }

    [AvaloniaFact]
    public void WidgetUsesTheOverlayCardsWithoutInternalIdentityOrPermanentArrangementButtons()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        PluginWidgetPin pin = new("test", "default", "power");
        PluginWidgetPreferences preferences = new(() => Task.FromResult(new[] { pin }),
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask);
        PinnedPluginWidgets panel = new(new MutableProvider(), (_, _) => { }, preferences);
        UiFixture.Named<StackPanel>(window, "PinnedPluginWidgetsHost").Children.Add(panel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(panel.GetLogicalDescendants().OfType<TextBlock>(),
            text => text.Text?.Contains("test / default") == true);
        Assert.All(panel.GetLogicalDescendants().OfType<Expander>(), expander => Assert.False(expander.IsExpanded));
        VisualBaseline.Verify(window, "overlay-widgets-1280");
    }

    [AvaloniaFact]
    public void ReorderAndUnpinKeepFocusOnTheAffectedWidgetOrNeighbor()
    {
        using UiFixture fixture = new();
        PluginWidgetPin first = new("missing", "default", "first");
        PluginWidgetPin second = new("missing", "default", "second");
        List<PluginWidgetPin> pins = [first, second];
        PluginWidgetPreferences preferences = new(() => Task.FromResult(pins.ToArray()),
            (pin, offset) => { PluginWidgetPins.Move(pins, pin, offset); return Task.CompletedTask; },
            pin => { pins.Remove(pin); return Task.CompletedTask; },
            () => { PluginWidgetPins.ResetOrder(pins); return Task.CompletedTask; });
        PinnedPluginWidgets panel = new(new MissingProvider(), (_, _) => { }, preferences);
        Window window = new() { Content = panel, Width = 600, Height = 700 };
        Button Find(PluginWidgetPin pin, string label) => panel.GetLogicalDescendants().OfType<Button>()
            .Single(button => Equals(button.Tag, (pin, label)));
        try
        {
            window.Show();
            foreach (var expander in panel.GetLogicalDescendants().OfType<Expander>()) { expander.IsExpanded = true; }
            UiFixture.Click(window, Find(first, "Move down"));
            Assert.Equal([second, first], pins);
            Assert.True(Find(first, "Unpin").IsFocused);
            UiFixture.Click(window, Find(first, "Unpin"));
            Assert.Equal([second], pins);
            Assert.True(Find(second, "Unpin").IsFocused);
            UiFixture.Click(window, Find(second, "Unpin"));
            Assert.Empty(pins);
            Assert.True(panel.IsFocused);
        }
        finally { window.Close(); }
    }

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
            source.Available = false;
            panel.Refresh();
            Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text == "Widget unavailable" && text.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    private sealed class MutableProvider : ICommonPluginOverlaySource
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
