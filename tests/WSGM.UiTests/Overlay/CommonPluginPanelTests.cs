using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
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
    public void PluginPageHasOnePinTogglePerWidgetAndSharedActionCards()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        var host = UiFixture.Named<StackPanel>(window, "CommonPluginRows");
        PluginWidgetPreferences preferences = new(() => Task.FromResult(Array.Empty<PluginWidgetPin>()),
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            () => Task.CompletedTask);
        host.Children.Add(new CommonPluginPanel(new MutableProvider(), preferences: preferences));
        var tile = UiFixture.Named<CardButton>(window, "SystemPluginsTile");
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
                .Single(button => button is CardButton { Title: "Change fan" });
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
                    .Single(button => button is CardButton { Title: "Change fan" }));
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
        var previous = KeyboardService.Handler;
        Action<string>? accept = null;
        KeyboardService.Handler = (prompt, initial, maximum, callback) =>
        {
            Assert.Equal("Command name", prompt);
            Assert.Equal("Power", initial);
            Assert.Equal(4096, maximum);
            accept = callback;
            return true;
        };
        Window window = new() { Width = 500, Height = 200 };
        try
        {
            var (editor, read) = CommonPluginPanel.CreateTextArgumentEditor(
                new PluginSetting("name", "Command name", PluginSettingKind.Text, new PluginValue(Text: "Power")));
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
            KeyboardService.Handler = previous;
        }
    }
}
