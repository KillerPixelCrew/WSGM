using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Settings;

public sealed class SettingsGraphicsInteractionTests
{
    [AvaloniaFact]
    public void EnablingIntelGraphicsAndSavingKeepsDeviceAndSteamControllerOwnership()
    {
        using UiFixture fixture = new();
        fixture.Saved.DeviceIntegration.Enabled = true;
        fixture.Saved.DeviceIntegration.ControllerManagementEnabled = true;
        fixture.Saved.Profiles.Global.ControllerTarget = ManagedControllerTarget.SteamDeckComposite;
        fixture.Saved.GpuDrivers = new GpuDriverConfig();
        var window = fixture.Settings();
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        var intel = AddIntelRow(model);
        Assert.True(model.DeviceIntegrationEnabled);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        var toggle = UiFixture.Named<Control>(window, "PageIntegration").GetVisualDescendants()
            .OfType<ToggleSwitch>().Single(control => ReferenceEquals(control.DataContext, intel));
        UiFixture.Click(window, toggle);
        Assert.True(intel.Enabled);
        Assert.True(model.DeviceIntegrationEnabled);
        var request = model.CaptureSaveRequest();
        Assert.DoesNotContain("DeviceIntegration.Enabled", request.SharedEdits);
        Save(window);
        Assert.True(fixture.Saved.GpuDrivers!.Intel);
        Assert.True(fixture.Saved.DeviceIntegration.Enabled);
        Assert.True(fixture.Saved.DeviceIntegration.ControllerManagementEnabled);
        Assert.Equal(ManagedControllerTarget.SteamDeckComposite, fixture.Saved.Profiles.Global.ControllerTarget);
        Save(window);
        Assert.True(fixture.Saved.DeviceIntegration.Enabled);
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        var master = UiFixture.Named<Control>(window, "PageDevice").GetVisualDescendants()
            .OfType<ToggleSwitch>().First();
        Assert.True(master.IsChecked);
    }

    [AvaloniaFact]
    public void EnablingIntelInAnOpenSettingsWindowKeepsDeviceOwnershipEnabledByAnotherSurface()
    {
        using UiFixture fixture = new();
        fixture.Saved.GpuDrivers = new GpuDriverConfig();
        var window = fixture.Settings();
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        var intel = AddIntelRow(model);
        fixture.Saved.DeviceIntegration.Enabled = true;
        fixture.Saved.Profiles.Global.ControllerTarget = ManagedControllerTarget.SteamDeckComposite;
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        var toggle = UiFixture.Named<Control>(window, "PageIntegration").GetVisualDescendants()
            .OfType<ToggleSwitch>().Single(control => ReferenceEquals(control.DataContext, intel));
        UiFixture.Click(window, toggle);
        Save(window);
        Assert.True(fixture.Saved.GpuDrivers!.Intel);
        Assert.True(fixture.Saved.DeviceIntegration.Enabled);
        Save(window);
        Assert.True(fixture.Saved.DeviceIntegration.Enabled);
    }

    private static CommonPluginInstanceRow AddIntelRow(SettingsViewModel model)
    {
        model.GraphicsDrivers.Clear();
        CommonPluginInstanceRow row = new("wsgm.gpu.intel", "default", "Intel graphics", false, true);
        model.GraphicsDrivers.Add(row);
        Dispatcher.UIThread.RunJobs();
        return row;
    }

    private static void Save(SettingsWindow window)
    {
        UiFixture.Click(window,
            window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save changes")));
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("Saved", Assert.IsType<SettingsViewModel>(window.DataContext).StatusText);
    }
}
