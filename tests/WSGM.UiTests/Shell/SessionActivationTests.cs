using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Shell;

public sealed class SessionActivationTests
{
    [AvaloniaFact]
    public async Task SettingsShortcutReachesTheResidentUiOwnerAndStopsAfterDisposal()
    {
        using UiFixture fixture = new();
        var name = "WSGM.Test.Settings." + Guid.NewGuid().ToString("N");
        Assert.False(SettingsActivation.TryRequest(name));
        var requests = 0;
        using (new SettingsActivation(() =>
               {
                   Assert.True(Dispatcher.UIThread.CheckAccess());
                   requests++;
               }, name))
        {
            Assert.True(SettingsActivation.TryRequest(name));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(1, requests);
            Assert.True(SettingsActivation.TryRequest(name));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(2, requests);
        }

        Assert.False(SettingsActivation.TryRequest(name));
    }

    [AvaloniaFact]
    public async Task SettingsRequestQueuedBeforeShutdownDoesNotOpenAfterDisposal()
    {
        using UiFixture fixture = new();
        var name = "WSGM.Test.Settings." + Guid.NewGuid().ToString("N");
        var requests = 0;
        using (new SettingsActivation(() => requests++, name))
        {
            Assert.True(SettingsActivation.TryRequest(name));
        }

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(0, requests);
    }

    [AvaloniaFact]
    public async Task ActivationQueuedDuringStartupReachesTheUiOwner()
    {
        using UiFixture fixture = new();
        var name = @"Local\WSGM.Test.Activate." + Guid.NewGuid().ToString("N");
        using EventWaitHandle signal = new(false, EventResetMode.AutoReset, name);
        signal.Set();
        TaskCompletionSource activated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using SessionActivation owner = new(() =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            activated.TrySetResult();
        }, name);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
