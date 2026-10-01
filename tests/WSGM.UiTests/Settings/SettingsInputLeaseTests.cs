using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Settings;

public sealed class SettingsInputLeaseTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusedSettingsHoldsTheLeaseAndDropsItOnClose(bool handoff)
    {
        using UiFixture fixture = new();
        List<(bool Hold, string Owner)> calls = [];
        fixture.HoldSteamInput = owner => calls.Add((true, owner));
        fixture.DropSteamInput = (owner, _) => calls.Add((false, owner));
        var window = fixture.Settings(gameModeSurface: handoff);
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        var owner = Assert.Single(calls.Where(call => call.Hold).Select(call => call.Owner).Distinct());

        UiFixture.Click(window, UiFixture.Tab(window, 5));
        Assert.True(UiFixture.Named<Control>(window, "PageQuickAccess").IsVisible);
        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
        Assert.Equal((false, owner), calls[^1]);
    }

    [AvaloniaFact]
    public void MinimizingDesktopSettingsDropsItsLease()
    {
        using UiFixture fixture = new();
        List<(bool Hold, string Owner)> calls = [];
        fixture.HoldSteamInput = owner => calls.Add((true, owner));
        fixture.DropSteamInput = (owner, _) => calls.Add((false, owner));
        var window = fixture.Settings();
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        var owner = Assert.Single(calls.Where(call => call.Hold).Select(call => call.Owner).Distinct());

        window.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((false, owner), calls[^1]);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void SettingsRespectsTheSavedLeaseOptOut()
    {
        using UiFixture fixture = new();
        fixture.Saved.SteamInputLeaseEnabled = false;
        fixture.HoldSteamInput = _ => throw new InvalidOperationException("Unexpected hold");
        var window = fixture.Settings(gameModeSurface: true);
        window.Activate();
        UiFixture.Key(window, Key.Escape);
        Assert.False(window.IsVisible);
    }
}
