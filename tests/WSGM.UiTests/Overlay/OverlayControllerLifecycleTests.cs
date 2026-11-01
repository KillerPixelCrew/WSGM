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
            controller.OnPinToggleRequested("system.keep-awake");
            Assert.False(controller.Pins.Writes.IsCompleted);
            // The watcher can still publish the first save while the second is queued.
            controller.ApplyConfig(new AppConfig { QuickAccessPins = ["home.steam"] });
            controller.OnPinToggleRequested("system.standby");
            Assert.Empty(config.QuickAccessPins);
        }
        finally
        {
            release.Set();
            await holdingStore.WaitAsync(TimeSpan.FromSeconds(5));
            await controller.Pins.Writes.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal([AppConfig.DesktopSwitchPin, "home.steam", "system.keep-awake", "system.standby"],
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
        controller.OnPinToggleRequested("system.keep-awake");
        await controller.Pins.Writes;
        Assert.Equal([AppConfig.DesktopSwitchPin, "home.steam"], config.QuickAccessPins);
        Assert.Equal([AppConfig.DesktopSwitchPin, "home.steam"], fixture.Store.Read().RequireConfig().QuickAccessPins);
    }

    [AvaloniaFact]
    public void TheDesktopSwitchCannotBeUnpinned()
    {
        using var fixture = new UiFixture();
        fixture.Store.Update(_ => true);
        var config = fixture.Store.Read().RequireConfig();
        using var audio = new AudioManager();
        using var radios = new RadioManager();
        using var drives = new RemovableDriveManager();
        var blocker = new SteamInputBlocker(new SteamInputShim(),
            () => throw new InvalidOperationException("Pin toggles must not acquire Steam Input."));
        using var controller = new OverlayController(config, fixture.Store, blocker, null,
            new SessionModes(config, null), audio, radios, drives, previewOnly: true);

        controller.OnPinToggleRequested(AppConfig.DesktopSwitchPin);

        Assert.Equal([AppConfig.DesktopSwitchPin], controller.Pins.Ids);
    }

    [AvaloniaFact]
    public async Task CloseRefocusAsksSteamToRaiseTheGameBeforeFocusingIt()
    {
        using var fixture = new UiFixture();
        fixture.Store.Update(_ => true);
        var config = fixture.Store.Read().RequireConfig();
        using var audio = new AudioManager();
        using var radios = new RadioManager();
        using var drives = new RemovableDriveManager();
        var blocker = new SteamInputBlocker(new SteamInputShim(),
            () => throw new InvalidOperationException("A close refocus must not acquire Steam Input."));
        using var controller = new OverlayController(config, fixture.Store, blocker, null,
            new SessionModes(config, null), audio, radios, drives, previewOnly: true);
        List<string> steps = [];
        var gameReturn = new GameWindowReturn((pid, _) =>
        {
            steps.Add($"raise {pid}");
            return Task.FromResult(true);
        }, _ => 42, _ => false, hwnd =>
        {
            steps.Add($"focus {hwnd}");
            return true;
        }, _ => { });

        await controller.RestoreFocusAsync(gameReturn, 30);

        Assert.Equal(["raise 42", "focus 30"], steps);
    }

    [AvaloniaFact]
    public async Task DisposingDuringSteamRaiseCancelsTheCloseRefocus()
    {
        using var fixture = new UiFixture();
        fixture.Store.Update(_ => true);
        var config = fixture.Store.Read().RequireConfig();
        using var audio = new AudioManager();
        using var radios = new RadioManager();
        using var drives = new RemovableDriveManager();
        var blocker = new SteamInputBlocker(new SteamInputShim(),
            () => throw new InvalidOperationException("A close refocus must not acquire Steam Input."));
        using var controller = new OverlayController(config, fixture.Store, blocker, null,
            new SessionModes(config, null), audio, radios, drives, previewOnly: true);
        TaskCompletionSource<bool> steam = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var gameReturn = new GameWindowReturn((_, token) => steam.Task.WaitAsync(token), _ => 42, _ => false,
            _ => throw new InvalidOperationException("A cancelled refocus must not focus."), _ => { });

        var refocus = controller.RestoreFocusAsync(gameReturn, 30);
        controller.Dispose();
        await refocus.WaitAsync(TimeSpan.FromSeconds(5));
        steam.SetResult(true);
    }
}
