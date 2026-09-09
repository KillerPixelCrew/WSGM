using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class DisplayRouteEditorTests
{
    [AvaloniaFact]
    public void CaptureAndDraftSelectionDoNotSaveOrDispatch()
    {
        using UiFixture fixture = new();
        int captures = 0, saves = 0;
        DisplayRouteBinding? saved = null;
        DisplayTargetIdentity target = new("tv", 1, 2, "TV", 0, 0, 1);
        DisplayRouteEditorServices services = new(
            () => [new(new("ir", "one"), new("send", "Send", [new("command", "Command", PluginSettingKind.Text,
                new(Text: "PC"), Choices: ["PC", "TV"])]), "IR: Send")],
            () => Task.FromResult(new DisplayRouteConfiguration()),
            () => { captures++; return Task.FromResult(new DisplayProfile(1, [target], [], [])); },
            (enabled, index, binding) =>
            {
                Assert.True(enabled);
                Assert.Equal(0, index);
                saves++;
                saved = binding;
                return Task.CompletedTask;
            });
        DisplayRouteEditor editor = new(services);
        Window window = new() { Content = editor, Width = 700, Height = 1200 };
        Button Button(string label) => editor.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
        try
        {
            window.Show();
            Assert.Equal(0, saves);
            editor.GetLogicalDescendants().OfType<CheckBox>().Single().IsChecked = true;
            var action = editor.GetLogicalDescendants().OfType<ComboBox>().Single(combo => combo.Items.OfType<DisplayRouteActionOption>().Any());
            action.SelectedIndex = 1;
            UiFixture.Click(window, Button("Capture current display profile"));
            Assert.Equal(1, captures);
            Assert.Equal(0, saves);
            UiFixture.Click(window, Button("Save route"));
            Assert.Equal(1, saves);
            Assert.Equal("send", saved!.ActionId);
            Assert.Equal("PC", saved.Arguments["command"].Text);
            Assert.Equal(target, saved.Target);
            Assert.Equal(target, Assert.Single(saved.Profile!.Targets));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void UnavailableBindingSurvivesSave()
    {
        using UiFixture fixture = new();
        DisplayRouteBinding original = new() { Plugin = new("missing", "one"), ActionId = "scene", Arguments = new() { ["name"] = new(Text: "TV") } };
        DisplayRouteConfiguration config = new() { EnterGameMode = original };
        DisplayRouteBinding? saved = null;
        DisplayRouteEditor editor = new(new(() => [], () => Task.FromResult(config),
            () => throw new InvalidOperationException("No capture requested."),
            (_, _, binding) => { saved = binding; return Task.CompletedTask; }));
        Window window = new() { Content = editor, Width = 700, Height = 1200 };
        try
        {
            window.Show();
            UiFixture.Click(window, editor.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save route")));
            Assert.Equal(original.Plugin, saved!.Plugin);
            Assert.Equal("TV", saved.Arguments["name"].Text);
        }
        finally { window.Close(); }
    }
}
