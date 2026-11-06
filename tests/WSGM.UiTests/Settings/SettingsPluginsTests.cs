using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Settings;

public sealed class SettingsPluginsTests
{
    [AvaloniaFact]
    public async Task AnOlderBundleRecordNeverBringsBackTheRetiredGraphicsOrDevicePackages()
    {
        using UiFixture fixture = new()
        {
            // What an install updated without setup still reads: the 2.1.0 bundle with its retired packages.
            PluginPage = new PluginPackagePage(PluginPackageCatalog.Empty, [], null,
            [
                Row("wsgm.gpu.intel", "Intel graphics", PluginPackageSection.Available),
                Row("wsgm.gpu.nvidia", "NVIDIA graphics", PluginPackageSection.Unavailable),
                Row("wsgm.gpu.amd", "AMD graphics", PluginPackageSection.Unavailable),
                Row("wsgm.device.msi.claw", "MSI Claw family", PluginPackageSection.Unavailable),
                Row("wsgm.ir", "IR remote", PluginPackageSection.Available)
            ])
        };
        var window = fixture.Settings(1024, 640);
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        await model.LoadPluginPackagesAsync();
        UiFixture.Click(window, UiFixture.Tab(window, 8));
        Dispatcher.UIThread.RunJobs();

        var page = UiFixture.Named<Control>(window, "PagePlugins");
        var names = page.GetVisualDescendants().OfType<TextBlock>()
            .Where(static text => text.Classes.Contains("plugin-name")).Select(static text => text.Text ?? "").ToArray();
        Assert.Equal(["IR remote"], names);
        Assert.False(model.HasUnavailablePackages);
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(),
            static text => text.Text is "Not for this device" && text.IsEffectivelyVisible);
    }

    private static PluginPackageRowState Row(string id, string name, PluginPackageSection section)
    {
        return new PluginPackageRowState(id, name, section, [], "", section == PluginPackageSection.Available
            ? PluginPackageAction.Install
            : PluginPackageAction.None, "");
    }
}
