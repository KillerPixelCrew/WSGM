using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Setup;
using WSGM.Setup.UI;
using WSGM.Testing;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class SetupCompatibilityCaptureTests
{
    [AvaloniaTheory]
    [InlineData(1024, 640, false)]
    [InlineData(1280, 800, true)]
    public void OptionalChoicesRemainReachable(int width, int height, bool unavailable)
    {
        using var fixture = new UiFixture();
        var page = new DriverCompatibilityPage(unavailable ? null
            : new WindowsProtectionState(true, true, ControlFlowGuardSetting.Default, false),
            unavailable ? "Windows protection settings could not be read." : null);
        var model = new SetupViewModel(new SetupOptions(), null, page,
            ["welcome", "profile", "customize", "compatibility", "progress", "summary"], 3);
        var window = new SetupWindow { Width = width, Height = height, DataContext = model };
        window.Styles.Add(new StyleInclude(new Uri("avares://WSGM.Setup/"))
        {
            Source = new Uri("avares://WSGM.Setup/UI/SetupTheme.axaml")
        });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, window.GetVisualDescendants().OfType<ToggleSwitch>().Count());
        Assert.All(page.Options, option => Assert.False(option.On));
        var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", $"setup-compatibility-{width}");
        Directory.CreateDirectory(directory);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "viewport.png"));
        window.Close();
    }
}
