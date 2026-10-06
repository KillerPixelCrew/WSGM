using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using WSGM.Tests.Fakes;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

/// <summary>The common plugin panel, its pinned widgets, choice editors and pin controls.</summary>
public sealed class CommonPluginPanelTests
{
    [AvaloniaFact]
    public void ReattachedPanelRefreshesAndInvokesWithALiveToken()
    {
        using var fixture = new UiFixture();
        var source = new TokenCheckingSource();
        var panel = new CommonPluginPanel(source, new PluginWidgetPin("test", "device", "fan"));
        var window = new Window { Content = panel, Width = 600, Height = 500 };
        try
        {
            window.Show();
            window.Content = null;
            window.Content = panel;
            Dispatcher.UIThread.RunJobs();
            panel.GetLogicalDescendants().OfType<Expander>().Single().IsExpanded = true;
            panel.GetLogicalDescendants().OfType<ComboBox>().Single().SelectedItem = "turbo";
            UiFixture.Click(window, panel.GetLogicalDescendants().OfType<ActionButton>()
                .Single(button => button.Title == "Change fan"));
            Assert.Equal("turbo", source.Requested);
            Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "Applied");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ReattachedPinnedWidgetsReadChangedPreferences()
    {
        using var fixture = new UiFixture();
        PluginWidgetPin[] pins = [new("test", "device", "fan")];
        var preferences = new PluginWidgetPreferences(() => Task.FromResult(pins),
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            () => Task.CompletedTask);
        var widgets = new PinnedPluginWidgets(new TokenCheckingSource(), (_, _) => { }, preferences);
        var window = new Window { Content = widgets, Width = 600, Height = 500 };
        try
        {
            window.Show();
            Assert.Single(widgets.GetLogicalDescendants().OfType<CommonPluginPanel>());
            window.Content = null;
            pins = [.. pins, new PluginWidgetPin("missing", "default", "power")];
            window.Content = widgets;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, widgets.GetLogicalDescendants().OfType<CommonPluginPanel>().Count());
            Assert.Contains(widgets.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text == "Plugin unavailable");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PluginRailPageHasOnePinTogglePerWidgetAndSharedActions()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var host = UiFixture.Named<StackPanel>(window, "CommonPluginRows");
        List<PluginWidgetPin> pins = [];
        PluginWidgetPreferences preferences = new(() => Task.FromResult(pins.ToArray()),
            (pin, pinned) =>
            {
                PluginWidgetPins.Set(pins, pin, pinned);
                return Task.CompletedTask;
            }, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            () => Task.CompletedTask);
        var source = new MutableProvider();
        var panel = new CommonPluginPanel(source, preferences: preferences);
        host.Children.Add(panel);
        var tile = UiFixture.Named<ActionButton>(window, "SystemPluginsTile");
        tile.IsVisible = true;
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemPlugins));
        var pinControl = Assert.Single(host.GetLogicalDescendants().OfType<PluginWidgetPinControls>());
        Assert.Empty(pins);
        Assert.Equal(0, source.Invocations);
        UiFixture.Click(window, pinControl);
        Assert.Equal(new PluginWidgetPin("test", "default", "power"), Assert.Single(pins));
        Assert.True(pinControl.IsPinned);
        UiFixture.Click(window, pinControl);
        Assert.Empty(pins);
        Assert.False(pinControl.IsPinned);

        // docs/overlay-and-input.md approves CollapsibleSection with a native Expander for
        // common plugin categories. The former custom folding-header pixel reference is obsolete.
        var section = Assert.Single(host.GetLogicalDescendants().OfType<CollapsibleSection>());
        Assert.False(section.IsExpanded);
        var heading = section.Heading;
        Assert.True(heading.Focus(NavigationMethod.Directional));
        Assert.True(window.NavigateWorkspace(NavigationDirection.Right));
        Assert.True(section.IsExpanded);
        Assert.Same(heading, window.FocusManager!.GetFocusedElement());
        var action = Assert.Single(section.Body.GetLogicalDescendants().OfType<ActionButton>());
        Assert.Equal("Run", action.Title);
        Assert.Equal("plugin.test.default.run", action.Tag);
        Assert.Equal(0, source.Invocations);
        UiFixture.Click(window, action);
        Assert.Equal(1, source.Invocations);
        panel.Refresh();
        Assert.Same(section, Assert.Single(host.GetLogicalDescendants().OfType<CollapsibleSection>()));
        Assert.Same(heading, section.Heading);
        Assert.Same(pinControl, Assert.Single(host.GetLogicalDescendants().OfType<PluginWidgetPinControls>()));
    }

    [AvaloniaFact]
    public void WidgetUsesTheOverlayCardsWithoutInternalIdentityOrPermanentArrangementButtons()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        PluginWidgetPin pin = new("test", "default", "power");
        PluginWidgetPreferences preferences = new(() => Task.FromResult(new[] { pin }),
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            () => Task.CompletedTask);
        PinnedPluginWidgets panel = new(new MutableProvider(), (_, _) => { }, preferences);
        UiFixture.Named<StackPanel>(window, "PinnedPluginWidgetsHost").Children.Add(panel);
        Dispatcher.UIThread.RunJobs();
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
            (pin, pinned) =>
            {
                PluginWidgetPins.Set(pins, pin, pinned);
                return Task.CompletedTask;
            },
            (pin, offset) =>
            {
                PluginWidgetPins.Move(pins, pin, offset);
                return Task.CompletedTask;
            },
            pin =>
            {
                pins.Remove(pin);
                return Task.CompletedTask;
            },
            () =>
            {
                PluginWidgetPins.ResetOrder(pins);
                return Task.CompletedTask;
            });
        PinnedPluginWidgets panel = new(new MissingProvider(), (_, _) => { }, preferences);
        Window window = new() { Content = panel, Width = 600, Height = 700 };
        try
        {
            window.Show();
            foreach (var expander in panel.GetLogicalDescendants().OfType<Expander>())
            {
                expander.IsExpanded = true;
            }

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
        finally
        {
            window.Close();
        }

        return;

        Button Find(PluginWidgetPin pin, string label)
        {
            return panel.GetLogicalDescendants().OfType<Button>()
                .Single(button => Equals(button.Tag, (pin, label)));
        }
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
        finally
        {
            window.Close();
        }
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
                text => text is { Text: "Widget unavailable", IsEffectivelyVisible: true });
        }
        finally
        {
            window.Close();
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
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ControllerOpensChoiceEditorAndAppliesDraft()
    {
        using UiFixture fixture = new();
        ChoiceSource source = new();
        CommonPluginPanel panel = new(source, new PluginWidgetPin("test", "device", "fan"));
        Window window = new() { Content = panel, Width = 600, Height = 500 };
        FakeButtonSource buttons = new();
        using GamepadNavigation navigation = new(buttons, window, () => { });
        try
        {
            window.Show();
            var expander = panel.GetLogicalDescendants().OfType<Expander>().Single();
            var header = expander.GetVisualDescendants().OfType<ToggleButton>().Single();
            Assert.True(header.Focus());
            buttons.Press(GamepadButtons.A);
            Assert.True(expander.IsExpanded);
            window.UpdateLayout();
            var choice = panel.GetLogicalDescendants().OfType<ComboBox>().Single();
            Assert.True(choice.Focus());
            buttons.Press(GamepadButtons.A);
            Assert.True(choice.IsDropDownOpen);
            buttons.Press(GamepadButtons.DPadDown);
            Assert.Equal("turbo", choice.SelectedItem);
            Assert.Null(source.Requested);
            buttons.Press(GamepadButtons.A);
            Assert.False(choice.IsDropDownOpen);
            var apply = panel.GetLogicalDescendants().OfType<Button>()
                .Single(button => button is ActionButton { Title: "Change fan" });
            Assert.True(apply.Focus());
            buttons.Press(GamepadButtons.A);
            Assert.Equal("turbo", source.Requested);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DevicePinPanelOffersPinsWithoutDuplicatingEditors()
    {
        using UiFixture fixture = new();
        ChoiceSource source = new();
        CommonPluginPanel panel = new(source, pinsOnly: true);
        Window window = new() { Content = panel, Width = 600, Height = 500 };
        try
        {
            window.Show();
            Assert.Single(panel.GetLogicalDescendants().OfType<PluginWidgetPinControls>());
            Assert.Empty(panel.GetLogicalDescendants().OfType<ComboBox>());
            Assert.Null(source.Requested);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ChoiceDraftDispatchesOnlyAfterExplicitAction()
    {
        using UiFixture fixture = new();
        ChoiceSource source = new();
        CommonPluginPanel panel = new(source, new PluginWidgetPin("test", "device", "fan"));
        Window window = new() { Content = panel, Width = 600, Height = 500 };
        try
        {
            window.Show();
            panel.GetLogicalDescendants().OfType<Expander>().Single().IsExpanded = true;
            var choice = panel.GetLogicalDescendants().OfType<ComboBox>().Single();
            Assert.Equal("quiet", choice.SelectedItem);
            choice.SelectedItem = "turbo";
            Assert.Null(source.Requested);
            UiFixture.Click(window,
                panel.GetLogicalDescendants().OfType<Button>()
                    .Single(button => button is ActionButton { Title: "Change fan" }));
            Assert.Equal("turbo", source.Requested);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void OnlyExplicitPinAndUnpinClicksChangePreferences()
    {
        using UiFixture fixture = new();
        List<bool> edits = [];
        PluginWidgetPinControls view = new("Remote", pinned =>
        {
            edits.Add(pinned);
            return Task.CompletedTask;
        });
        Window window = new() { Content = view, Width = 500, Height = 200 };
        try
        {
            window.Show();
            Assert.Empty(edits);
            UiFixture.Click(window, view);
            Assert.True(view.IsPinned);
            UiFixture.Click(window, view);
            Assert.False(view.IsPinned);
            Assert.Equal([true, false], edits);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ActionTextDraftUsesControllerKeyboardAndChangesOnlyOnAcceptance()
    {
        using UiFixture fixture = new();
        Action<string>? accept = null;
        Func<string, string, int, Action<string>, bool> requestText = (prompt, initial, maximum, callback) =>
        {
            Assert.Equal("Command name", prompt);
            Assert.Equal("Power", initial);
            Assert.Equal(0, maximum);
            accept = callback;
            return true;
        };
        Window window = new() { Width = 500, Height = 200 };
        try
        {
            var (editor, read) = CommonPluginPanel.CreateTextArgumentEditor(
                new PluginSetting("name", "Command name", PluginSettingKind.Text, new PluginValue(Text: "Power")),
                requestText);
            window.Content = editor;
            window.Show();
            UiFixture.Click(window, editor);
            Assert.NotNull(accept);
            Assert.Equal("Power", read().Text);
            accept("HDMI 1");
            Assert.Equal("HDMI 1", read().Text);
            Assert.Equal("HDMI 1", editor.Content);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class TokenCheckingSource : ICommonPluginOverlaySource
    {
        private readonly ChoiceSource _source = new();
        internal string? Requested => _source.Requested;

        public PluginOverlayInstance[] Snapshot()
        {
            return _source.Snapshot();
        }

        public PluginStatePublication[] State(PluginInstanceIdentity identity)
        {
            return _source.State(identity);
        }

        public Task<PluginActionResult> InvokeAsync(PluginInstanceIdentity identity, long generation, string action,
            IReadOnlyDictionary<string, PluginValue> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _source.InvokeAsync(identity, generation, action, arguments, cancellationToken);
        }
    }
}
