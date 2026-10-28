using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using WSGM.Setup;
using WSGM.Setup.UI;
using WSGM.Testing;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class SetupRestartSummaryCaptureTests
{
    [AvaloniaTheory]
    [InlineData(1024, 640)]
    [InlineData(1280, 800)]
    public void ANeededRestartIsThePrimaryActionWithAStartLaterChoice(int width, int height)
    {
        using var fixture = new UiFixture();
        var page = new SummaryPage("Done", "WSGM is ready", "Restart Windows to finish. Everything else already works.",
            [], "", "Restart now", "Start WSGM, restart later", true, true);
        var model = new SetupViewModel(new SetupOptions(), null, page,
            ["welcome", "profile", "customize", "compatibility", "progress", "summary"], 5);
        var window = new SetupWindow { Width = width, Height = height, DataContext = model };
        window.Styles.Add(new StyleInclude(new Uri("avares://WSGM.Setup/"))
        {
            Source = new Uri("avares://WSGM.Setup/UI/SetupTheme.axaml")
        });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var primary = UiFixture.Named<Button>(window, "PrimaryButton");
        var back = UiFixture.Named<Button>(window, "BackButton");
        Assert.True(page.OffersRestart);
        Assert.Equal("Restart now", primary.Content);
        Assert.Equal("Start WSGM, restart later", back.Content);
        Assert.True(primary.IsEffectivelyVisible && back.IsEffectivelyVisible);
        Assert.True(back.Bounds.Right <= primary.Bounds.Left);
        var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", $"setup-restart-{width}");
        Directory.CreateDirectory(directory);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "viewport.png"));
        window.Close();
    }
}
