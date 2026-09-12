using WindowsDeviceControl;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DisplayArrivalWaiterTests
{
    private static readonly DisplayTargetIdentity Tv = new(@"\\?\tv", null, null, "Living room TV", 0, 0, 1);
    private static readonly DisplayTargetIdentity Desk = new(@"\\?\desk", null, null, "Desk", 0, 0, 2);

    private static DisplayArrangement Seen(string fingerprint, params DisplayTargetIdentity[] available) =>
        new([.. available.Select(target => new DisplayTargetObservation(target, true, false, null))],
            fingerprint, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task AnAbsentDisplayIsWaitedForUntilItAppearsAndSettles()
    {
        Presence presence = new([Seen("a", Desk), Seen("b", Desk, Tv), Seen("b", Desk, Tv)]);
        Signal signal = new();

        DisplayArrangement settled = await Waiter(presence, signal).WaitAsync([Tv], default);

        Assert.Equal("b", settled.Fingerprint);
        Assert.Equal(1, signal.Waits);
    }

    [Fact]
    public async Task AFlappingTopologyIsNotActedOnUntilTwoObservationsAgree()
    {
        // A monitor coming up behind a switch enumerates, changes and re-enumerates. Acting on the
        // first sighting would configure a display that is still negotiating.
        Presence presence = new([Seen("a", Tv), Seen("b", Tv), Seen("c", Tv), Seen("c", Tv)]);

        DisplayArrangement settled = await Waiter(presence, new()).WaitAsync([Tv], default);

        Assert.Equal("c", settled.Fingerprint);
        Assert.Equal(4, presence.Reads);
    }

    [Fact]
    public async Task ADisplayThatDisappearsAgainRestartsTheWait()
    {
        Presence presence = new([Seen("a", Tv), Seen("b", Desk), Seen("c", Tv), Seen("c", Tv)]);
        Signal signal = new();

        DisplayArrangement settled = await Waiter(presence, signal).WaitAsync([Tv], default);

        Assert.Equal("c", settled.Fingerprint);
        Assert.Equal(1, signal.Waits);
    }

    [Fact]
    public async Task AQueryThatThrowsIsADriverMidChangeRatherThanAFailure()
    {
        Presence presence = new([null, Seen("a", Tv), Seen("a", Tv)]);

        DisplayArrangement settled = await Waiter(presence, new()).WaitAsync([Tv], default);

        Assert.Equal("a", settled.Fingerprint);
    }

    [Fact]
    public async Task AnEmptyTargetListReturnsTheFirstSettledObservation()
    {
        Presence presence = new([Seen("a"), Seen("a")]);

        Assert.Equal("a", (await Waiter(presence, new()).WaitAsync([], default)).Fingerprint);
    }

    [Fact]
    public async Task CancellationIsTheOnlyWayOutOfAWaitThatNeverSucceeds()
    {
        Presence presence = new([Seen("a", Desk)]) { Repeat = true };
        using CancellationTokenSource cancellation = new();
        Signal signal = new() { OnWait = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Waiter(presence, signal).WaitAsync([Tv], cancellation.Token));
    }

    [Fact]
    public void MissingNamesOnlyTheDisplaysTheObservationCannotSee()
    {
        DisplayArrangement seen = Seen("a", Desk);

        Assert.Equal([Tv], DisplayArrivalWaiter.Missing(seen, [Tv, Desk]));
        Assert.True(DisplayArrivalWaiter.Present(seen, [Desk]));
        Assert.False(DisplayArrivalWaiter.Present(seen, [Tv]));
    }

    [Fact]
    public void AConnectedButInactiveDisplayCountsAsPresent()
    {
        // Windows enumerates a monitor before it is part of the desktop; the layout apply is what
        // activates it, so waiting for Active would wait forever.
        DisplayArrangement seen = new([new(Tv, true, false, null)], "a", DateTimeOffset.UnixEpoch);

        Assert.True(DisplayArrivalWaiter.Present(seen, [Tv]));
    }

    private static DisplayArrivalWaiter Waiter(Presence presence, Signal signal) =>
        // The settle delay is real time in production and nothing here needs to spend it; the
        // waiter's ordering is what these tests are about.
        new(presence, signal, static (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

    private sealed class Presence(IReadOnlyList<DisplayArrangement?> observations) : IDisplayPresence
    {
        internal int Reads { get; private set; }

        internal bool Repeat { get; init; }

        public DisplayArrangement Observe()
        {
            int index = Repeat ? Math.Min(Reads, observations.Count - 1) : Reads;
            Reads++;
            if (index >= observations.Count)
            {
                // Deliberately a type the waiter does not treat as "mid-change": a script that
                // runs out must fail the test rather than turn into an endless wait.
                throw new ArgumentOutOfRangeException(nameof(observations),
                    "The waiter looked more times than the test scripted.");
            }
            return observations[index]
                ?? throw new System.ComponentModel.Win32Exception(31, "the driver is mid-change");
        }
    }

    private sealed class Signal : IDisplayChangeSignal
    {
        internal int Waits { get; private set; }

        internal Action? OnWait { get; init; }

        public Task WaitForChangeAsync(TimeSpan backstop, CancellationToken cancellationToken)
        {
            Waits++;
            OnWait?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
