using Avalonia.Headless.XUnit;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class OverlayControllerLifecycleTests
{
    [AvaloniaFact]
    public async Task PendingPinSavesSurviveAnEarlierConfigReloadAndKeepPressOrder()
    {
        using var fixture = new UiFixture();
        fixture.Store.Update(_ => true);
        var config = new AppConfig { QuickAccessPins = [] };
        using var audio = new AudioManager();
        using var radios = new RadioManager();
        using var drives = new RemovableDriveManager();
        var blocker = new SteamInputBlocker(new SteamInputShim(),
            () => throw new InvalidOperationException("Pin saves must not acquire Steam Input."));
        using var controller = new OverlayController(config, fixture.Store, blocker, null,
            new SessionModes(config, null), audio, radios, drives);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holdingStore = Task.Run(() => fixture.Store.Update(_ =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The pin-save test did not release its store transaction.");
            }

            return false;
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            controller.OnPinToggleRequested("home.steam");
            controller.OnPinToggleRequested("home.desktop");
            Assert.False(controller.PinWrites.IsCompleted);
            // The watcher can still publish the first save while the second is queued.
            controller.ApplyConfig(new AppConfig { QuickAccessPins = ["home.steam"] });
            controller.OnPinToggleRequested("system.standby");
            Assert.Empty(config.QuickAccessPins);
        }
        finally
        {
            release.Set();
            await holdingStore.WaitAsync(TimeSpan.FromSeconds(5));
            await controller.PinWrites.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(["home.steam", "home.desktop", "system.standby"],
            fixture.Store.Read().RequireConfig().QuickAccessPins);
        Assert.Empty(config.QuickAccessPins);
    }

    [AvaloniaFact]
    public async Task PreviewPinChangesNeverPersistOrMutateTheSuppliedConfig()
    {
        using var fixture = new UiFixture();
        fixture.Store.Update(config =>
        {
            config.QuickAccessPins = ["home.steam"];
            return true;
        });
        var config = fixture.Store.Read().RequireConfig();
        using var audio = new AudioManager();
        using var radios = new RadioManager();
        using var drives = new RemovableDriveManager();
        var blocker = new SteamInputBlocker(new SteamInputShim(),
            () => throw new InvalidOperationException("A pin preview must not acquire Steam Input."));
        using var controller = new OverlayController(config, fixture.Store, blocker, null,
            new SessionModes(config, null), audio, radios, drives, previewOnly: true);
        controller.OnPinToggleRequested("home.steam");
        controller.OnPinToggleRequested("home.desktop");
        await controller.PinWrites;
        Assert.Equal(["home.steam"], config.QuickAccessPins);
        Assert.Equal(["home.steam"], fixture.Store.Read().RequireConfig().QuickAccessPins);
    }
}
