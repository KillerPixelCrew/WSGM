using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Settings;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Settings;

public sealed class SettingsStartupTests
{
    private static readonly SteamAutostartSource SteamEntry = new(
        SteamAutostartKind.ScheduledTask, SteamAutostartScope.Machine,
        "Fixture Steam", "Steam", true);

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
            if (model.TakeOverSteamAutostartCommand.CanExecute(null))
            {
                completed.TrySetResult();
            }
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
            if (model.TakeOverSteamAutostartCommand.CanExecute(null))
            {
                completed.TrySetResult();
            }
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
        finally
        {
            release.TrySetResult();
        }

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scans);
        Assert.Contains("Windows has no Steam startup entry", model.SteamAutostartStatusText);
    }
}
