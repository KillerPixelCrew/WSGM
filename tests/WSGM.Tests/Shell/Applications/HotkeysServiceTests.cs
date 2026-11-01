using WSGM.Core;
using WSGM.Interop;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class HotkeysServiceTests
{
    private const nint Game = 0x100;
    private const nint Desktop = 0x200;

    private static async Task<(HotkeysService Service, HotkeyDefinition Hotkey)> CreateAsync(
        TemporaryConfigStore store, FakeDesktop desktop, int delay = 0)
    {
        // These tests follow one hotkey; a new configuration's two default hotkeys would only add noise.
        store.Store.Update(config =>
        {
            config.Hotkeys = [];
            return true;
        });
        var service = new HotkeysService(store.Store, store.Store.Read().RequireConfig(), desktop);
        var hotkey = new HotkeyDefinition
        {
            Name = "Lossless Scaling", Keys = [HotkeyKeys.Control, HotkeyKeys.Alt, HotkeyKeys.S],
            PressDelayMs = delay
        };
        await service.SaveAsync(hotkey);
        return (service, hotkey);
    }

    [Fact]
    public async Task WithNoGameInFrontNothingIsTypedAndTheHotkeySaysWhy()
    {
        using var store = new TemporaryConfigStore();
        var desktop = new FakeDesktop { ForegroundWindow = Desktop };
        var (service, hotkey) = await CreateAsync(store, desktop);

        await service.SendAsync(hotkey.Id, 0, CancellationToken.None);

        Assert.Empty(desktop.Sent);
        Assert.Empty(desktop.Raised);
        Assert.Equal(HotkeysService.NoGameNotice, Assert.Single(service.ReadState()).Notice);
    }

    [Fact]
    public async Task AnOverlayOpenedOverTheDesktopNeverTypesIntoIt()
    {
        using var store = new TemporaryConfigStore();
        var desktop = new FakeDesktop { ForegroundWindow = Desktop };
        var (service, hotkey) = await CreateAsync(store, desktop);

        await service.SendAsync(hotkey.Id, Desktop, CancellationToken.None);

        Assert.Empty(desktop.Raised);
        Assert.Empty(desktop.Sent);
        Assert.Equal(HotkeysService.NoGameNotice, Assert.Single(service.ReadState()).Notice);
    }

    [Fact]
    public async Task AGameThatDoesNotComeBackToTheFrontReceivesNothing()
    {
        using var store = new TemporaryConfigStore();
        var desktop = new FakeDesktop { ForegroundWindow = Desktop };
        var (service, hotkey) = await CreateAsync(store, desktop);

        await service.SendAsync(hotkey.Id, Game, CancellationToken.None);

        Assert.Equal([Game], desktop.Raised);
        Assert.Empty(desktop.Sent);
        Assert.Equal(HotkeysService.NoGameNotice, Assert.Single(service.ReadState()).Notice);
    }

    [Fact]
    public async Task TheGameInFrontReceivesTheKeysInOrderWithTheDelay()
    {
        using var store = new TemporaryConfigStore();
        var desktop = new FakeDesktop { ForegroundWindow = Game };
        var (service, hotkey) = await CreateAsync(store, desktop, 120);

        await service.SendAsync(hotkey.Id, Game, CancellationToken.None);

        var (keys, delay) = Assert.Single(desktop.Sent);
        Assert.Equal([0x11u, 0x12u, 0x53u], keys);
        Assert.Equal(120, delay);
        Assert.Empty(desktop.Raised);
        Assert.Equal("", Assert.Single(service.ReadState()).Notice);
    }

    [Fact]
    public async Task TheGameTheSheetOpenedOverIsBroughtBackBeforeTheKeysAreSent()
    {
        using var store = new TemporaryConfigStore();
        var desktop = new FakeDesktop { ForegroundWindow = Desktop, RaiseMovesForeground = true };
        var (service, hotkey) = await CreateAsync(store, desktop);

        await service.SendAsync(hotkey.Id, Game, CancellationToken.None);

        Assert.Equal([Game], desktop.Raised);
        Assert.Single(desktop.Sent);
    }

    [Fact]
    public async Task APartialSendIsReportedOnTheHotkey()
    {
        using var store = new TemporaryConfigStore();
        var desktop = new FakeDesktop
        {
            ForegroundWindow = Game, Result = new KeyboardInput.SendResult(1, 6, 5)
        };
        var (service, hotkey) = await CreateAsync(store, desktop);

        await service.SendAsync(hotkey.Id, Game, CancellationToken.None);

        Assert.Contains("1 of 6", Assert.Single(service.ReadState()).Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingRefusesAHotkeyWithoutKeysAndAnEditClearsTheNotice()
    {
        using var store = new TemporaryConfigStore();
        var desktop = new FakeDesktop { ForegroundWindow = Desktop };
        var (service, hotkey) = await CreateAsync(store, desktop);
        await service.SendAsync(hotkey.Id, 0, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(hotkey.Copy() with { Keys = [] }));
        await service.SaveAsync(hotkey.Copy() with { Keys = [HotkeyKeys.Insert], Name = " Menu " });

        var state = Assert.Single(service.ReadState());
        Assert.Equal("", state.Notice);
        Assert.Equal("Menu", state.Definition.Name);
        Assert.Equal([HotkeyKeys.Insert], store.Store.Read().RequireConfig().Hotkeys.Single().Keys);
    }

    [Fact]
    public async Task DeletingAHotkeyRemovesItsPin()
    {
        using var store = new TemporaryConfigStore();
        var (service, hotkey) = await CreateAsync(store, new FakeDesktop());
        store.Store.Update(config =>
        {
            config.QuickAccessPins.Add(HotkeysService.PinPrefix + hotkey.Id);
            return true;
        });

        await service.RemoveAsync(hotkey.Id);

        var config = store.Store.Read().RequireConfig();
        Assert.Empty(config.Hotkeys);
        Assert.DoesNotContain(HotkeysService.PinPrefix + hotkey.Id, config.QuickAccessPins);
        Assert.Empty(service.ReadState());
    }

    private sealed class FakeDesktop : IHotkeyDesktop
    {
        internal nint ForegroundWindow { get; set; }
        internal bool RaiseMovesForeground { get; init; }
        internal KeyboardInput.SendResult Result { get; init; } = new(6, 6, 0);
        internal List<nint> Raised { get; } = [];
        internal List<(uint[] Keys, int Delay)> Sent { get; } = [];

        public nint Foreground()
        {
            return ForegroundWindow;
        }

        public HotkeyTarget? Describe(nint window)
        {
            return window == Game ? new HotkeyTarget(Game, 42, "game.exe") : null;
        }

        public void BringToFront(nint window)
        {
            Raised.Add(window);
            if (RaiseMovesForeground)
            {
                ForegroundWindow = window;
            }
        }

        public KeyboardInput.SendResult Send(IReadOnlyList<uint> keys, int pressDelayMs)
        {
            Sent.Add(([.. keys], pressDelayMs));
            return Result;
        }
    }
}
