using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using WSGM.Settings;

namespace WSGM.UiTests;

public sealed class SettingsInputLeaseTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusedSettingsLeasesAndClosesWhileNativeAcquireIsPending(bool handoff)
    {
        using UiFixture fixture = new();
        TaskCompletionSource<string> acquiring = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim finish = new();
        fixture.AcquireSteamInput = owner =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            acquiring.TrySetResult(owner);
            Assert.True(finish.Wait(TimeSpan.FromSeconds(10)));
        };
        fixture.ReleaseSteamInput = (owner, _) =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            released.TrySetResult(owner);
        };
        SettingsWindow window = fixture.Settings(gameModeSurface: handoff);
        try
        {
            await acquiring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            UiFixture.Click(window, UiFixture.Tab(window, 5));
            Assert.True(UiFixture.Named<Control>(window, "PageQuickAccess").IsVisible);
            UiFixture.Key(window, Key.Escape);
            Assert.False(window.IsVisible);
            Assert.False(released.Task.IsCompleted);
        }
        finally { finish.Set(); }
        Assert.Equal(await acquiring.Task, await released.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public async Task MinimizingDesktopSettingsReleasesItsLease()
    {
        using UiFixture fixture = new();
        TaskCompletionSource acquiring = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AcquireSteamInput = _ => acquiring.TrySetResult();
        fixture.ReleaseSteamInput = (_, _) => released.TrySetResult();
        SettingsWindow window = fixture.Settings();
        window.Activate();
        await acquiring.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.WindowState = WindowState.Minimized;
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void SettingsRespectsTheSavedLeaseOptOut()
    {
        using UiFixture fixture = new();
        fixture.Saved.SteamInputLeaseEnabled = false;
        fixture.ClaimSteamInput = _ => throw new InvalidOperationException("Unexpected claim");
        fixture.AcquireSteamInput = _ => throw new InvalidOperationException("Unexpected acquisition");
        SettingsWindow window = fixture.Settings(gameModeSurface: true);
        window.Activate();
        UiFixture.Key(window, Key.Escape);
        Assert.False(window.IsVisible);
    }
}
