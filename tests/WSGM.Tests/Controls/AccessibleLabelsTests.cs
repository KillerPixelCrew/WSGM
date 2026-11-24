using Avalonia.Automation;
using Avalonia.Controls;
using WSGM.Controls;

namespace WSGM.Tests.Controls;

/// <summary>An editor without an accessible name takes the title of the row it sits in.</summary>
public sealed class AccessibleLabelsTests
{
    [Fact]
    public void ASettingsRowSwitchIsNamedByItsTitleAndDescribedByItsCaption()
    {
        var (row, title, toggle) = SettingsRow("Start Steam unelevated", "Runs Steam at normal integrity.",
            new ToggleSwitch());

        AccessibleLabels.Label(toggle);

        Assert.Equal("Start Steam unelevated", AutomationProperties.GetName(toggle));
        Assert.Equal("Runs Steam at normal integrity.", AutomationProperties.GetHelpText(toggle));
        Assert.NotNull(row);

        // The name follows the title, so a changed title is read as changed.
        title.Text = "Start Steam at normal integrity";
        Assert.Equal("Start Steam at normal integrity", AutomationProperties.GetName(toggle));
    }

    [Fact]
    public void AnExplicitNameIsKept()
    {
        ComboBox combo = new();
        AutomationProperties.SetName(combo, "Launch mode for new Game Library titles");
        SettingsRow("New titles launch with", "A multiplayer title starts controller only.", combo);

        AccessibleLabels.Label(combo);

        Assert.Equal("Launch mode for new Game Library titles", AutomationProperties.GetName(combo));
    }

    [Fact]
    public void ATextButtonKeepsItsTextAndSaysWhichRowItActsOn()
    {
        Button open = new() { Content = "Open" };
        SettingsRow("Library", "Filter tabs and SD-card libraries", open);

        AccessibleLabels.Label(open);

        Assert.False(open.IsSet(AutomationProperties.NameProperty));
        Assert.Equal("Library", AutomationProperties.GetHelpText(open));
    }

    [Fact]
    public void AnEditorInARowWithoutATitleDoesNotTakeANeighboursTitle()
    {
        StackPanel group = new();
        group.Children.Add(SettingsRow("Restart Steam when it exits", "", new ToggleSwitch()).Row);
        ToggleSwitch untitled = new();
        group.Children.Add(new Border { Classes = { "setting-row" }, Child = untitled });
        group.Children.Add(SettingsRow("Steam integration", "", new ToggleSwitch()).Row);

        AccessibleLabels.Label(untitled);

        Assert.False(untitled.IsSet(AutomationProperties.NameProperty));
    }

    [Fact]
    public void AnEditorInsideAGroupOfTitledRowsIsNotNamedAfterOneOfThem()
    {
        StackPanel group = new();
        group.Children.Add(new TextBlock { Text = "First", Classes = { "setting-title" } });
        group.Children.Add(new TextBlock { Text = "Second", Classes = { "setting-title" } });
        StackPanel inputs = new();
        ToggleSwitch toggle = new();
        inputs.Children.Add(toggle);
        group.Children.Add(inputs);

        AccessibleLabels.Label(toggle);

        Assert.False(toggle.IsSet(AutomationProperties.NameProperty));
    }

    [Fact]
    public void ALabelJustBeforeAnEditorNamesIt()
    {
        StackPanel field = new();
        field.Children.Add(new TextBlock { Text = "Recording device", Classes = { "caption" } });
        ComboBox combo = new();
        field.Children.Add(combo);

        AccessibleLabels.Label(combo);

        Assert.Equal("Recording device", AutomationProperties.GetName(combo));
    }

    private static (Border Row, TextBlock Title, Control Editor) SettingsRow(string title, string caption,
        Control editor)
    {
        TextBlock heading = new() { Text = title, Classes = { "setting-title" } };
        StackPanel text = new() { Children = { heading, new TextBlock { Text = caption, Classes = { "caption" } } } };
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
        return (new Border { Classes = { "setting-row" }, Child = grid }, heading, editor);
    }
}
