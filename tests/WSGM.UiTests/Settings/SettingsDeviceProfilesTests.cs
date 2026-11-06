using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibHandheld.Contracts;
using WSGM.Core;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Settings;

public sealed class SettingsDeviceProfilesTests
{
    private const string Device = "msi.claw8";
    private const string Family = "msi-claw";

    [AvaloniaFact]
    public void TheEditorListsProfilesBesideTheOneItEditsAndRemoveAsksFirst()
    {
        using var fixture = Fixture();
        var window = Open(fixture);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        var page = UiFixture.Named<Control>(window, "PageDeviceProfiles");
        model.SelectedDeviceProfile = model.DeviceProfiles[0];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, page.GetVisualDescendants().OfType<ListBoxItem>().Count());
        VisualBaseline.Verify(window, "settings-device-profiles-1024");

        UiFixture.Click(window, UiFixture.Named<Button>(page, "RemoveProfile"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiFixture.Named<Control>(page, "RemoveConfirm").IsVisible);
        Assert.Same(UiFixture.Named<Button>(page, "KeepProfile"), window.FocusManager!.GetFocusedElement());
        UiFixture.Click(window, UiFixture.Named<Button>(page, "KeepProfile"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, model.DeviceProfiles.Count);

        UiFixture.Click(window, UiFixture.Named<Button>(page, "RemoveProfile"));
        UiFixture.Click(window, page.GetVisualDescendants().OfType<Button>()
            .First(static button => Equals(button.Content, "Remove") && button.IsEffectivelyVisible));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, model.DeviceProfiles.Count);
        Assert.Equal("1 unsaved change", model.UnsavedChangesText);
    }

    [AvaloniaFact]
    public void ResetToDefaultPutsBackTheDefaultCurve()
    {
        using var fixture = Fixture();
        var window = Open(fixture);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        var page = UiFixture.Named<Control>(window, "PageDeviceProfiles");
        model.SelectedDeviceProfile = model.DeviceProfiles[0];
        Dispatcher.UIThread.RunJobs();

        UiFixture.Click(window, page.GetVisualDescendants().OfType<Button>()
            .Single(static button => Equals(button.Content, "Reset to default")));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SettingsViewModel.DefaultFanCurve, model.DeviceProfiles[0].Curve);
        Assert.Equal("1 unsaved change", model.UnsavedChangesText);
    }

    private static SettingsWindow Open(UiFixture fixture)
    {
        var window = fixture.Settings(1024, 640);
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Named<Button>(UiFixture.Named<Control>(window, "PageDevice"), "OpenProfiles")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static UiFixture Fixture()
    {
        UiFixture fixture = new() { Inventory = new SettingsInventory(new HandheldDefinition(Device, Family, "MSI Claw", "msi-claw"), []) };
        fixture.Saved.DeviceIntegration = new DeviceIntegrationConfig
        {
            PreferencesSchemaVersion = DeviceIntegrationConfig.CurrentPreferencesSchemaVersion,
            DeviceProfiles =
            [
                new DeviceProfileScope
                {
                    DeviceDefinitionId = Device,
                    FamilyId = Family,
                    Profiles =
                    [
                        Curve("quiet", "Quiet nights", [(30, 20), (50, 25), (70, 50), (85, 80), (95, 100)]),
                        Curve("gaming", "Gaming", [(30, 30), (60, 60), (80, 100)]),
                        new DeviceAuthoredProfile
                        {
                            ProfileId = "ember", Name = "Ember", CapabilityId = "lighting.color", Curve = [],
                            Color = 0xFF6A2B
                        }
                    ]
                }
            ]
        };
        return fixture;
    }

    private static DeviceAuthoredProfile Curve(string id, string name, (int Input, int Output)[] points)
    {
        return new DeviceAuthoredProfile
        {
            ProfileId = id,
            Name = name,
            CapabilityId = "thermal.fan-curve",
            Curve = [.. points.Select(static point => new AuthoredCurvePoint { Input = point.Input, Output = point.Output })]
        };
    }
}
