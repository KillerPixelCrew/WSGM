using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using WSGM.Input;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class PluginWidgetChoiceTests
{
    [AvaloniaFact]
    public void ControllerOpensChoiceEditorAndAppliesDraft()
    {
        using UiFixture fixture = new();
        ChoiceSource source = new();
        CommonPluginPanel panel = new(source, new PluginWidgetPin("test", "device", "fan"));
        Window window = new() { Content = panel, Width = 600, Height = 500 };
        Buttons buttons = new();
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
            var apply = panel.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Change fan"));
            Assert.True(apply.Focus());
            buttons.Press(GamepadButtons.A);
            Assert.Equal("turbo", source.Requested);
        }
        finally { window.Close(); }
    }

    private sealed class Buttons : IUiButtonSource
    {
        public event Action<GamepadButtons>? ButtonPressed;
        internal void Press(GamepadButtons buttons) => ButtonPressed?.Invoke(buttons);
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
        finally { window.Close(); }
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
            UiFixture.Click(window, panel.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Change fan")));
            Assert.Equal("turbo", source.Requested);
        }
        finally { window.Close(); }
    }

    private sealed class ChoiceSource : ICommonPluginOverlaySource
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
}
