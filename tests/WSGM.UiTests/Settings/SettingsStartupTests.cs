using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Settings;

public sealed class SettingsStartupTests
{
    private static readonly SteamAutostartSource SteamEntry = new(
        SteamAutostartKind.ScheduledTask, SteamAutostartScope.Machine,
        "Fixture Steam", "Steam", "fixture-steam.exe", true);

    [AvaloniaFact]
    public async Task FirstLaunchShowsSetupAndCanCloseWhileStartupScanIsPending()
    {
        using UiFixture fixture = new();
        fixture.Saved.QuickSetupRevision = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ScanSteamAutostart = () =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            started.SetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return [SteamEntry];
        };
        var window = fixture.Settings(1024, 640);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(UiFixture.Named<Control>(window, "QuickSetupOverlay").IsVisible);
            Assert.False(UiFixture.Named<Control>(window, "SettingsRoot").IsEnabled);
            Assert.False(UiFixture.Named<Button>(window, "QuickSetupContinueButton").IsEnabled);
            Assert.True(UiFixture.Named<Button>(window, "QuickSetupSkipButton").IsEffectivelyEnabled);
            UiFixture.Key(window, Key.Escape);
            Assert.False(window.IsVisible);
            Assert.Contains("input-stop", fixture.Calls);
        }
        finally { release.TrySetResult(); }
        await window.SteamAutostartScan.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(UiFixture.Named<Control>(window, "QuickSetupAutostartRow").IsVisible);
        Assert.DoesNotContain("save", fixture.Calls);
    }

    [AvaloniaFact]
    public async Task FirstLaunchRequiresConsentForDetectedStartupEntries()
    {
        using UiFixture fixture = new();
        fixture.Saved.QuickSetupRevision = 0;
        fixture.ScanSteamAutostart = () => [SteamEntry];
        var window = fixture.Settings(1024, 640);
        await window.SteamAutostartScan.WaitAsync(TimeSpan.FromSeconds(5));
        var next = UiFixture.Named<Button>(window, "QuickSetupContinueButton");
        Assert.False(next.IsEnabled);
        Assert.Contains("Fixture Steam", UiFixture.Named<TextBlock>(window, "QuickSetupAutostartList").Text);
        UiFixture.Click(window, UiFixture.Named<CheckBox>(window, "QuickSetupAutostart"));
        Assert.True(next.IsEnabled);
        next.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var position = next.TranslatePoint(default, window)!.Value;
        Assert.InRange(position.Y, 0, window.ClientSize.Height - next.Bounds.Height);
        Assert.DoesNotContain("save", fixture.Calls);
    }

    [AvaloniaFact]
    public async Task FailedStartupScanCanBeRetriedWithoutLeavingSetup()
    {
        using UiFixture fixture = new();
        fixture.Saved.QuickSetupRevision = 0;
        fixture.ScanSteamAutostart = () => throw new IOException("fixture scan failure");
        var window = fixture.Settings();
        await window.SteamAutostartScan.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(UiFixture.Named<Button>(window, "QuickSetupContinueButton").IsEnabled);
        Assert.Contains("Could not check", UiFixture.Named<TextBlock>(window, "QuickSetupScanStatus").Text);
        fixture.ScanSteamAutostart = () => [];
        UiFixture.Click(window, UiFixture.Named<Button>(window, "QuickSetupScanRetry"));
        await window.SteamAutostartScan.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(UiFixture.Named<Button>(window, "QuickSetupContinueButton").IsEnabled);
        Assert.False(UiFixture.Named<Control>(window, "QuickSetupScanStatus").IsVisible);
        Assert.DoesNotContain("save", fixture.Calls);
    }

    [AvaloniaFact]
    public async Task AcceptedTakeoverAppliesOffDispatcherAndPublishesItsReadbackOnDispatcher()
    {
        using UiFixture fixture = new();
        fixture.Saved.SteamAutostartTakeoverAccepted = true;
        fixture.ScanSteamAutostart = () => [SteamEntry];
        var writes = 0;
        fixture.ApplySteamAutostart = sources =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            Assert.Equal(SteamEntry, Assert.Single(sources));
            Interlocked.Increment(ref writes);
            return new SteamAutostartTakeoverResult([SteamEntry], [], []);
        };
        var window = fixture.Settings();
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        TaskCompletionSource completed = new();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.SteamAutostartStatusText))
            {
                Assert.True(Dispatcher.UIThread.CheckAccess());
            }
        };
        model.TakeOverSteamAutostartCommand.CanExecuteChanged += (_, _) =>
        {
            if (model.TakeOverSteamAutostartCommand.CanExecute(null)) { completed.TrySetResult(); }
        };
        model.TakeOverSteamAutostartCommand.Execute(null);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, writes);
        Assert.StartsWith("Turned off 1", model.SteamAutostartStatusText);
    }

    [AvaloniaFact]
    public async Task TakeOverAgainScansOffDispatcherAndDoesNotRepeatWhilePending()
    {
        using UiFixture fixture = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        fixture.ScanSteamAutostart = () =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            Interlocked.Increment(ref scans);
            started.SetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return [];
        };
        var window = fixture.Settings();
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        TaskCompletionSource completed = new();
        model.TakeOverSteamAutostartCommand.CanExecuteChanged += (_, _) =>
        {
            if (model.TakeOverSteamAutostartCommand.CanExecute(null)) { completed.TrySetResult(); }
        };
        try
        {
            model.TakeOverSteamAutostartCommand.Execute(null);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(model.TakeOverSteamAutostartCommand.CanExecute(null));
            model.TakeOverSteamAutostartCommand.Execute(null);
            UiFixture.Click(window, UiFixture.Tab(window, 1));
            Assert.True(UiFixture.Named<Control>(window, "PageSteam").IsVisible);
        }
        finally { release.TrySetResult(); }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scans);
        Assert.Contains("Windows has no Steam startup entry", model.SteamAutostartStatusText);
    }
}
