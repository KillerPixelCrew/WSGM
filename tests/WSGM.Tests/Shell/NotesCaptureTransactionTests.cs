using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>Add Screenshot: Armed, Countdown, Capturing, Restoring, and every way out of them.</summary>
public sealed class NotesCaptureTransactionTests
{
    private static readonly NotesCaptureTarget Game = new(4321, 77, 0, "game.exe");
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private sealed class Surface : INotesCaptureSurface
    {
        internal bool ReturnResult { get; init; } = true;
        internal int Returns { get; private set; }
        internal List<string> Reopened { get; } = [];

        public Task<bool> ReturnToGameAsync(NotesCaptureTarget target, CancellationToken cancellationToken)
        {
            Returns++;
            return Task.FromResult(ReturnResult);
        }

        public Task<bool> ReopenAsync(NotesCaptureTarget target, string pageId, CancellationToken cancellationToken)
        {
            Reopened.Add(pageId);
            return Task.FromResult(true);
        }
    }

    private sealed class Countdown : INotesCountdownDisplay
    {
        internal List<int> Shown { get; } = [];
        internal int Hides { get; private set; }
        internal bool Visible { get; private set; }

        public void Show(int remaining, string game)
        {
            Shown.Add(remaining);
            Visible = true;
        }

        public void Hide()
        {
            Hides++;
            Visible = false;
        }
    }

    private sealed class Harness
    {
        internal Surface Surface { get; init; } = new();
        internal Countdown Countdown { get; } = new();
        internal List<(string Page, int Width)> Inserted { get; } = [];
        internal NotesCaptureFrame Frame { get; set; } = new(Jpeg, 1920, 1080, null);
        internal Func<uint, bool> Alive { get; set; } = _ => true;
        internal Func<int, Task>? OnTick { get; set; }
        internal int Captures { get; private set; }
        private int _ticks;

        internal NotesCaptureTransaction Transaction { get; }

        internal Harness()
        {
            Transaction = new NotesCaptureTransaction((page, _, width, _) =>
            {
                Inserted.Add((page, width));
                return new NoteEditResult(NoteEditStatus.Applied, null, null, "element");
            }, () => { });
        }

        internal NotesCapturePorts Ports => new(Surface, Countdown, (_, _) =>
        {
            Captures++;
            return Task.FromResult(Frame);
        }, pid => Alive(pid), async (delay, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (delay >= TimeSpan.FromSeconds(1) && OnTick is not null)
            {
                await OnTick(++_ticks);
            }

            token.ThrowIfCancellationRequested();
        });
    }

    [Fact]
    public async Task ACompletedCaptureCountsDownInsertsAndReturnsToTheSamePage()
    {
        var harness = new Harness();

        var status = await harness.Transaction.RunAsync(NotesCaptureOrigin.Overlay, "page", 7, Game, harness.Ports,
            CancellationToken.None);

        Assert.Equal(1, harness.Surface.Returns);
        Assert.Equal([7, 6, 5, 4, 3, 2, 1], harness.Countdown.Shown);
        Assert.False(harness.Countdown.Visible);
        Assert.Equal([("page", 1920)], harness.Inserted);
        Assert.Equal(["page"], harness.Surface.Reopened);
        Assert.Equal(NotesCapturePhase.Idle, status.Phase);
        Assert.False(status.OutcomeIsError);
        Assert.Equal("element", status.InsertedElementId);
    }

    [Fact]
    public async Task ADesktopCaptureNeedsNoRunningGameAndReturnsToTheSamePage()
    {
        var harness = new Harness { Alive = _ => throw new InvalidOperationException("A desktop has no game process.") };
        var desktop = new NotesCaptureTarget(ProcessId: 0, Window: 0, SteamAppId: 0, Description: "desktop", Monitor: 99);
        NotesCaptureTarget? captured = null;
        var ports = harness.Ports with
        {
            Capture = (target, _) =>
            {
                captured = target;
                Assert.False(harness.Countdown.Visible);
                return Task.FromResult(harness.Frame);
            }
        };

        var status = await harness.Transaction.RunAsync(NotesCaptureOrigin.Overlay, "page", 5, desktop, ports,
            CancellationToken.None);

        Assert.Same(desktop, captured);
        Assert.Equal([5, 4, 3, 2, 1], harness.Countdown.Shown);
        Assert.Equal([("page", 1920)], harness.Inserted);
        Assert.Equal(["page"], harness.Surface.Reopened);
        Assert.Equal(NotesCapturePhase.Idle, status.Phase);
        Assert.False(status.OutcomeIsError);
    }

    [Fact]
    public async Task CancellingADesktopCountdownCapturesNothing()
    {
        var harness = new Harness { Alive = _ => false };
        var desktop = new NotesCaptureTarget(ProcessId: 0, Window: 0, SteamAppId: 0, Description: "desktop", Monitor: 99);
        harness.OnTick = _ =>
        {
            Assert.True(harness.Transaction.Cancel("Screenshot cancelled."));
            return Task.CompletedTask;
        };

        var status = await harness.Transaction.RunAsync(NotesCaptureOrigin.Overlay, "page", 5, desktop, harness.Ports,
            CancellationToken.None);

        Assert.Equal(0, harness.Captures);
        Assert.Empty(harness.Inserted);
        Assert.Empty(harness.Surface.Reopened);
        Assert.Equal("Screenshot cancelled.", status.Outcome);
        Assert.False(harness.Countdown.Visible);
    }

    [Fact]
    public async Task TheCountdownIsHeldBetweenFiveAndTenSeconds()
    {
        var harness = new Harness();

        await harness.Transaction.RunAsync(NotesCaptureOrigin.Overlay, "page", 30, Game, harness.Ports,
            CancellationToken.None);

        Assert.Equal(10, harness.Countdown.Shown[0]);
    }

    [Fact]
    public async Task AFailedCaptureInsertsNothingAndReturnsToTheIntactPage()
    {
        var harness = new Harness { Frame = NotesCaptureFrame.Failed("game.exe was not in front.") };

        var status = await harness.Transaction.RunAsync(NotesCaptureOrigin.Steam, "page", 5, Game, harness.Ports,
            CancellationToken.None);

        Assert.Empty(harness.Inserted);
        Assert.Equal(["page"], harness.Surface.Reopened);
        Assert.True(status.OutcomeIsError);
        Assert.Equal("game.exe was not in front.", status.Outcome);
    }

    [Fact]
    public async Task CancellingDuringTheCountdownCapturesNothing()
    {
        var harness = new Harness();
        harness.OnTick = tick =>
        {
            if (tick == 2)
            {
                Assert.True(harness.Transaction.Cancel("Screenshot cancelled."));
            }

            return Task.CompletedTask;
        };

        var status = await harness.Transaction.RunAsync(NotesCaptureOrigin.Overlay, "page", 5, Game, harness.Ports,
            CancellationToken.None);

        Assert.Equal(0, harness.Captures);
        Assert.Empty(harness.Inserted);
        // The surface that cancelled is already in front; nothing reopens over it.
        Assert.Empty(harness.Surface.Reopened);
        Assert.Equal("Screenshot cancelled.", status.Outcome);
        Assert.False(harness.Countdown.Visible);
    }

    [Fact]
    public async Task TheGameExitingDuringTheCountdownFailsBackToNotes()
    {
        var harness = new Harness();
        var alive = true;
        harness.Alive = _ => alive;
        harness.OnTick = tick =>
        {
            alive = tick < 2;
            return Task.CompletedTask;
        };

        var status = await harness.Transaction.RunAsync(NotesCaptureOrigin.Overlay, "page", 5, Game, harness.Ports,
            CancellationToken.None);

        Assert.Equal(0, harness.Captures);
        Assert.True(status.OutcomeIsError);
        Assert.Equal(["page"], harness.Surface.Reopened);
    }

    [Fact]
    public async Task NotReachingTheGameFailsBeforeAnyCountdown()
    {
        var harness = new Harness { Surface = new Surface { ReturnResult = false } };

        var status = await harness.Transaction.RunAsync(NotesCaptureOrigin.Steam, "page", 5, Game, harness.Ports,
            CancellationToken.None);

        Assert.Empty(harness.Countdown.Shown);
        Assert.True(status.OutcomeIsError);
        Assert.Equal(["page"], harness.Surface.Reopened);
    }

    [Fact]
    public async Task ShutdownEndsTheCountdownWithoutReopeningAnything()
    {
        var harness = new Harness();
        using CancellationTokenSource lifetime = new();
        harness.OnTick = tick =>
        {
            if (tick == 1)
            {
                lifetime.Cancel();
            }

            return Task.CompletedTask;
        };

        await harness.Transaction.RunAsync(NotesCaptureOrigin.Overlay, "page", 5, Game, harness.Ports, lifetime.Token);

        Assert.Equal(0, harness.Captures);
        Assert.Empty(harness.Surface.Reopened);
        Assert.False(harness.Countdown.Visible);
        Assert.False(harness.Transaction.Pending);
    }

    [Fact]
    public async Task ASecondScreenshotIsRefusedWhileOneIsPending()
    {
        var harness = new Harness();
        TaskCompletionSource release = new();
        harness.OnTick = _ => release.Task;
        var first = harness.Transaction.RunAsync(NotesCaptureOrigin.Overlay, "page", 5, Game, harness.Ports,
            CancellationToken.None);

        var second = await harness.Transaction.RunAsync(NotesCaptureOrigin.Steam, "other", 5, Game, harness.Ports,
            CancellationToken.None);
        release.SetResult();
        await first;

        Assert.Equal("A screenshot is already pending.", second.Outcome);
        Assert.Equal([("page", 1920)], harness.Inserted);
    }
}
