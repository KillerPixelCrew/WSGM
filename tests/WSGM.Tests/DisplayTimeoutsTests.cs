using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>
/// The session's display-off timeout owner, over a fake power scheme: what Steam's report raises,
/// what a choice from Steam's Screensaver settings or the overlay may write, and what the rows show.
/// </summary>
public sealed class DisplayTimeoutsTests
{
    [Fact]
    public async Task AReportRaisesADisplayTimeoutBelowTheScreensaverOnceAndLeavesTheRestAlone()
    {
        FakeScheme scheme = new() { [PowerTimeoutKind.DisplayAc] = 180, [PowerTimeoutKind.DisplayDc] = 900 };
        DisplayTimeouts timeouts = scheme.Owner();
        int changes = 0;
        timeouts.Changed += () => changes++;

        SteamUiCommandResult result = await timeouts.ReportAsync(new(300, null, Battery: false), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(300, scheme[PowerTimeoutKind.DisplayAc]);
        Assert.Equal(900, scheme[PowerTimeoutKind.DisplayDc]);
        Assert.Equal([(PowerTimeoutKind.DisplayAc, 300)], scheme.Writes);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task NeverIsLaterThanAnyScreensaverAndIsNotRaised()
    {
        FakeScheme scheme = new() { [PowerTimeoutKind.DisplayAc] = 0, [PowerTimeoutKind.DisplayDc] = 0 };

        await scheme.Owner().ReportAsync(new(3600, 3600, Battery: true), CancellationToken.None);

        Assert.Empty(scheme.Writes);
    }

    [Fact]
    public async Task ARefusedRaiseIsNotRetriedWithinTheReport()
    {
        FakeScheme scheme = new() { [PowerTimeoutKind.DisplayAc] = 60, [PowerTimeoutKind.DisplayDc] = 60 };
        scheme.Refuse = true;

        SteamUiCommandResult result = await scheme.Owner().ReportAsync(new(300, null, false), CancellationToken.None);

        // The report itself is heard; each breach got exactly one attempt.
        Assert.True(result.Succeeded);
        Assert.Equal(2, scheme.Writes.Count);
        Assert.Equal(60, scheme[PowerTimeoutKind.DisplayAc]);
    }

    [Fact]
    public async Task AChoiceBelowTheScreensaverIsRefusedWithItsReason()
    {
        FakeScheme scheme = new() { [PowerTimeoutKind.DisplayAc] = 600, [PowerTimeoutKind.DisplayDc] = 600 };
        DisplayTimeouts timeouts = scheme.Owner();
        await timeouts.ReportAsync(new(300, 900, Battery: true), CancellationToken.None);
        scheme.Writes.Clear();

        SteamUiCommandResult refused = await timeouts.SetTimeoutAsync("battery", 600, CancellationToken.None);
        SteamUiCommandResult applied = await timeouts.SetTimeoutAsync("plugged-in", 300, CancellationToken.None);
        SteamUiCommandResult never = await timeouts.SetTimeoutAsync("battery", 0, CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal("The display cannot turn off before Steam's screensaver starts (15 min).", refused.Error);
        Assert.True(applied.Succeeded);
        Assert.True(never.Succeeded);
        Assert.Equal([(PowerTimeoutKind.DisplayAc, 300), (PowerTimeoutKind.DisplayDc, 0)], scheme.Writes);
    }

    [Fact]
    public async Task AnUnknownRowIsRefusedAndWindowsRefusingIsReported()
    {
        FakeScheme scheme = new() { [PowerTimeoutKind.DisplayAc] = 600 };
        DisplayTimeouts timeouts = scheme.Owner();

        SteamUiCommandResult unknown = await timeouts.SetTimeoutAsync("sleep", 600, CancellationToken.None);
        scheme.Refuse = true;
        SteamUiCommandResult refused = await timeouts.SetTimeoutAsync("plugged-in", 900, CancellationToken.None);

        Assert.Equal("That timeout row is not one of WSGM's.", unknown.Error);
        Assert.Equal("Windows did not accept the display timeout.", refused.Error);
    }

    [Fact]
    public async Task CyclingADisplayTimeoutSkipsForbiddenPresetsButSleepCyclesFreely()
    {
        FakeScheme scheme = new()
        {
            [PowerTimeoutKind.DisplayAc] = 60,
            [PowerTimeoutKind.SleepAc] = 60,
        };
        DisplayTimeouts timeouts = scheme.Owner();
        await timeouts.ReportAsync(new(600, null, false), CancellationToken.None);
        // The report raised the display timeout to the bound already.
        Assert.Equal(600, scheme[PowerTimeoutKind.DisplayAc]);
        scheme[PowerTimeoutKind.DisplayAc] = 3600;

        Assert.True(timeouts.Cycle(PowerTimeoutKind.DisplayAc));
        Assert.True(timeouts.Cycle(PowerTimeoutKind.DisplayAc));
        Assert.True(timeouts.Cycle(PowerTimeoutKind.SleepAc));

        Assert.Equal(600, scheme[PowerTimeoutKind.DisplayAc]);
        Assert.Equal(180, scheme[PowerTimeoutKind.SleepAc]);
    }

    [Fact]
    public void CyclingRefusesToWriteBlindWhenWindowsGivesNoReading()
    {
        FakeScheme scheme = new();

        Assert.False(scheme.Owner().Cycle(PowerTimeoutKind.DisplayDc));
        Assert.Empty(scheme.Writes);
    }

    [Fact]
    public async Task TheRowsOfferOnlyAllowedChoicesAndNameTheBound()
    {
        FakeScheme scheme = new() { [PowerTimeoutKind.DisplayAc] = 900 };
        DisplayTimeouts timeouts = scheme.Owner();
        SteamScreensaverState before = timeouts.ReadState();
        await timeouts.ReportAsync(new(300, null, false), CancellationToken.None);

        SteamScreensaverState state = timeouts.ReadState();

        Assert.Equal(["battery", "plugged-in"], state.Rows.Select(row => row.Id));
        SteamTimeoutRow battery = state.Rows[0];
        Assert.False(battery.Available);
        Assert.Empty(battery.Options);
        SteamTimeoutRow pluggedIn = state.Rows[1];
        Assert.True(pluggedIn.Available);
        Assert.Equal(900, pluggedIn.Seconds);
        Assert.Equal([300, 600, 900, 1800, 3600, 0], pluggedIn.Options.Select(option => option.Seconds));
        Assert.Equal("Never", pluggedIn.Options[^1].Label);
        Assert.Equal("Not before the screensaver starts (5 min)", pluggedIn.Description);
        Assert.Equal(string.Empty, before.Rows[1].Description);
        Assert.True(state.Revision > before.Revision);
    }

    private sealed class FakeScheme
    {
        private readonly Dictionary<PowerTimeoutKind, int> _values = [];

        internal List<(PowerTimeoutKind Kind, int Seconds)> Writes { get; } = [];

        internal bool Refuse { get; set; }

        internal int this[PowerTimeoutKind kind]
        {
            get => _values[kind];
            set => _values[kind] = value;
        }

        internal DisplayTimeouts Owner() => new(
            kind => _values.TryGetValue(kind, out int seconds) ? seconds : null,
            (kind, seconds) =>
            {
                Writes.Add((kind, seconds));
                if (Refuse)
                {
                    return false;
                }
                _values[kind] = seconds;
                return true;
            });
    }
}
