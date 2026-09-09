using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class PluginWidgetChoiceTests
{
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
