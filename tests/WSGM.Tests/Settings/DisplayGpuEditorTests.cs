using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;
using WSGM.Settings;

namespace WSGM.Tests.Settings;

public sealed class DisplayGpuEditorTests
{
    private static readonly DisplayTargetIdentity Tv = new("tv", null, null, "TV", 1, 0, 7);

    [Fact]
    public void ActualDriverChoicesAreEditableWithoutReadbackAndAreNotSavedAsImplicitPreferences()
    {
        var capability = Color(Tv);
        DisplayLayoutEditor editor = new(() => { });
        editor.Load([new KnownDisplay { Target = Tv, GpuCapabilities = [capability] }], [Tv], null);

        var control = Assert.Single(Assert.Single(editor.Rows).GpuControls);
        Assert.True(control.CanEdit);
        Assert.Equal(["8", "10"], control.Choices.Select(choice => choice.Value));
        Assert.Empty(editor.BuildGpuPreferences());

        control.UseSetting = true;
        control.Choice = control.Choices[1];
        var saved = Assert.Single(editor.BuildGpuPreferences());
        Assert.Equal(Tv, saved.Target);
        Assert.Equal("10", saved.Value.Text);
    }

    [Fact]
    public void DisconnectedDisplayAndChangingDriverInstanceKeepTheSavedChoice()
    {
        DisplayLayoutEditor editor = new(() => { });
        editor.Load([new KnownDisplay { Target = Tv, GpuCapabilities = [Color(Tv)] }], [], null);
        editor.LoadGpuPreferences([Preference(Tv, "10")]);
        var refreshed = Color(Tv);
        refreshed.Descriptor = refreshed.Descriptor! with { InstanceId = "new-output-route" };
        editor.RefreshGpuCapabilities([refreshed]);

        var row = Assert.Single(editor.Rows);
        Assert.False(row.Present);
        var control = Assert.Single(row.GpuControls);
        Assert.True(control.CanEdit);
        Assert.True(control.UseSetting);
        Assert.Equal("10", control.Choice?.Value);
        var saved = Assert.Single(editor.BuildGpuPreferences());
        Assert.Equal("new-output-route", saved.InstanceId);
        Assert.Equal("10", saved.Value.Text);
    }

    [Fact]
    public void MissingControlAndUnsupportedSavedChoiceRemainConfiguredWithoutInventingDriverChoices()
    {
        DisplayLayoutEditor editor = new(() => { });
        editor.Load([new KnownDisplay { Target = Tv, GpuCapabilities = [Color(Tv)] }], [Tv], null);
        editor.LoadGpuPreferences([Preference(Tv, "12")]);
        var control = Assert.Single(Assert.Single(editor.Rows).GpuControls);
        Assert.Null(control.Choice);
        Assert.True(control.HasUnavailableChoice);
        Assert.Equal(["8", "10"], control.Choices.Select(choice => choice.Value));
        Assert.Equal("12", Assert.Single(editor.BuildGpuPreferences()).Value.Text);

        editor.RefreshGpuCapabilities([]);
        Assert.Empty(Assert.Single(editor.Rows).GpuControls);
        Assert.Equal("12", Assert.Single(editor.BuildGpuPreferences()).Value.Text);
    }

    [Fact]
    public void UndoIncludesExplicitGraphicsEditsAndGlobalControlsKeepTheirDriverWideScope()
    {
        var global = new DisplayGpuCapability
        {
            PluginId = "wsgm.gpu.nvidia",
            Descriptor = new CapabilityDescriptor
            {
                CapabilityId = "graphics.gsync", InstanceId = "driver", Role = CapabilityRole.GenericToggle,
                ValueKind = CapabilityValueKind.Boolean,
                Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "G-SYNC enabled" },
                SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent
            }
        };
        DisplayLayoutEditor editor = new(() => { });
        editor.Load([new KnownDisplay { Target = Tv, GpuCapabilities = [Color(Tv)] }], [Tv], null);
        editor.RefreshGpuCapabilities([Color(Tv), global]);
        var color = Assert.Single(Assert.Single(editor.Rows).GpuControls);
        color.UseSetting = true;
        color.Choice = color.Choices[1];
        editor.Undo();
        Assert.Equal("8", Assert.Single(editor.Rows[0].GpuControls).Choice?.Value);
        editor.Undo();
        Assert.Empty(editor.BuildGpuPreferences());

        var vrr = Assert.Single(editor.GlobalGpuControls);
        vrr.UseSetting = true;
        vrr.BooleanValue = true;
        Assert.Null(Assert.Single(editor.BuildGpuPreferences()).Target);
        Assert.True(Assert.Single(editor.BuildGpuPreferences()).Value.Boolean);
    }

    [Fact]
    public void DuplicateMonitorSerialsCannotMoveAControlToAnotherPhysicalOutput()
    {
        var first = Tv with { DevicePath = "first", EdidIdentity = "duplicate" };
        var second = Tv with { DevicePath = "second", TargetId = 8, EdidIdentity = "duplicate" };
        DisplayLayoutEditor editor = new(() => { });
        editor.Load([new KnownDisplay { Target = first }, new KnownDisplay { Target = second }], [first, second], null);
        editor.RefreshGpuCapabilities([Color(second)]);

        Assert.Empty(editor.Rows[0].GpuControls);
        Assert.Single(editor.Rows[1].GpuControls);
        editor.LoadGpuPreferences([Preference(first, "10")]);
        Assert.False(Assert.Single(editor.Rows[1].GpuControls).UseSetting);
        Assert.Equal(first, Assert.Single(editor.BuildGpuPreferences()).Target);
    }

    [Fact]
    public void ReplacementMonitorAtTheSamePathCannotConsumeTheOriginalOfflinePreference()
    {
        var original = Tv with { EdidIdentity = "original-serial" };
        var replacement = Tv with { EdidIdentity = "replacement-serial" };
        DisplayLayoutEditor editor = new(() => { });
        editor.Load([new KnownDisplay { Target = replacement, GpuCapabilities = [Color(replacement)] }],
            [replacement], null);
        editor.LoadGpuPreferences([Preference(original, "10")]);

        var control = Assert.Single(Assert.Single(editor.Rows).GpuControls);
        Assert.False(control.UseSetting);
        Assert.Equal("8", control.Choice?.Value);
        var saved = Assert.Single(editor.BuildGpuPreferences());
        Assert.Equal(original, saved.Target);
        Assert.Equal("original-serial", saved.Target?.EdidIdentity);
        Assert.Equal("10", saved.Value.Text);

        editor.RefreshGpuCapabilities([Color(replacement)]);
        Assert.False(Assert.Single(editor.Rows[0].GpuControls).UseSetting);
        Assert.Equal(original, Assert.Single(editor.BuildGpuPreferences()).Target);
    }

    private static DisplayGpuCapability Color(DisplayTargetIdentity target)
    {
        return new DisplayGpuCapability
        {
            Target = target, PluginId = "wsgm.gpu.nvidia",
            Descriptor = new CapabilityDescriptor
            {
                CapabilityId = "display.color.depth", InstanceId = "output", Role = CapabilityRole.GenericChoice,
                ValueKind = CapabilityValueKind.Choice,
                Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Color depth" },
                SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent,
                Choices =
                [
                    new CapabilityChoice("8", new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "8-bit" }),
                    new CapabilityChoice("10",
                        new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "10-bit" })
                ]
            }
        };
    }

    private static DisplayGpuPreference Preference(DisplayTargetIdentity target, string value)
    {
        return new DisplayGpuPreference
        {
            Target = target, PluginId = "wsgm.gpu.nvidia", CapabilityId = "display.color.depth",
            InstanceId = "old-output",
            Value = new PluginValue(Text: value)
        };
    }
}
