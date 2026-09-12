using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class GameModeEntryTransactionTests
{
    private static readonly DisplayTargetIdentity Tv = new(@"\\?\tv", null, null, "Living room TV", 0, 0, 1);
    private static readonly DisplayTargetIdentity Desk = new(@"\\?\desk", null, null, "Desk", 0, 0, 2);

    private static DisplayLayout Layout(DisplayTargetIdentity target) =>
        new([new(target, 0, 0, 1920, 1080, DisplayRefresh.FromHertz(60))]);

    private static PluginActionStep Step(string id) =>
        new() { Plugin = new("wsgm.ir", "blaster"), ActionId = id };

    private static GameModeLaunchConfiguration Custom() => new()
    {
        Kind = GameModeLaunchKind.Custom,
        GameLayout = Layout(Tv),
        WaitForDisplay = Tv,
        EnterActions = [Step("switch-to-pc"), Step("tv-on")],
        LeaveActions = [Step("switch-to-box")],
    };

    [Fact]
    public async Task ACustomEntryRunsItsStepsInOrderAndAsksForBigPictureAfterExplorerLeaves()
    {
        Backend backend = new();

        var result = await new GameModeEntryTransaction(backend, Custom()).RunAsync(default);

        Assert.Equal(GameModeEntryOutcome.Entered, result.Outcome);
        Assert.Equal(
        [
            "observe",
            "enter-actions",
            "wait",
            "persist-return",
            "prepare-explorer",
            "observe",
            "exit-explorer",
            "apply-layout",
            "big-picture",
            "commit",
        ], backend.Steps);
    }

    [Fact]
    public async Task ADefaultEntryTouchesNoLayoutButStillRunsTheActionsAndTheWait()
    {
        // The maintainer's desktop needs the IR switch and the TV wait even when the layout itself
        // is left to Windows, so Default is "no saved layout", not "no automation".
        Backend backend = new();
        GameModeLaunchConfiguration launch = new()
        {
            Kind = GameModeLaunchKind.Default,
            WaitForDisplay = Tv,
            EnterActions = [Step("switch-to-pc")],
        };

        var result = await new GameModeEntryTransaction(backend, launch).RunAsync(default);

        Assert.Equal(GameModeEntryOutcome.Entered, result.Outcome);
        Assert.Equal(
            ["enter-actions", "wait", "prepare-explorer", "observe", "exit-explorer",
             "default-posture", "big-picture", "commit"],
            backend.Steps);
    }

    [Fact]
    public async Task AFailedEntryActionStopsTheRestAndUndoesWhatRan()
    {
        Backend backend = new() { FailAction = "tv-on" };

        var result = await new GameModeEntryTransaction(backend, Custom()).RunAsync(default);

        Assert.Equal(GameModeEntryOutcome.Failed, result.Outcome);
        Assert.Contains("tv-on refused", result.Warning!, StringComparison.Ordinal);
        Assert.DoesNotContain("exit-explorer", backend.Calls);
        // The switch was already told to select this PC, so leaving it there would strand the TV
        // on the wrong input.
        Assert.Contains("leave-actions", backend.Calls);
        Assert.Contains("apply-layout", backend.Calls);
        Assert.Contains("clear-return", backend.Calls);
    }

    [Fact]
    public async Task AnEntryWhoseFirstActionWasRejectedDoesNotEmitTheLeaveActions()
    {
        // Nothing was sent, so there is nothing to undo, and an IR burst here would move a switch
        // the user never asked to move.
        Backend backend = new() { FailAction = "switch-to-pc" };

        await new GameModeEntryTransaction(backend, Custom()).RunAsync(default);

        Assert.DoesNotContain("leave-actions", backend.Calls);
    }

    [Fact]
    public async Task CancellingDuringTheDisplayWaitRestoresTheDesktopAndNeverTouchesExplorer()
    {
        using CancellationTokenSource cancellation = new();
        Backend backend = new() { OnWait = cancellation.Cancel };

        var result = await new GameModeEntryTransaction(backend, Custom()).RunAsync(cancellation.Token);

        Assert.Equal(GameModeEntryOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Warning);
        Assert.DoesNotContain("prepare-explorer", backend.Calls);
        Assert.DoesNotContain("exit-explorer", backend.Calls);
        Assert.Contains("leave-actions", backend.Calls);
        Assert.Equal(["captured"], backend.RestoredLayouts);
    }

    [Fact]
    public async Task ARefusedExplorerTakeoverKeepsTheDesktopAndCompensates()
    {
        Backend backend = new() { PrepareExplorer = false };

        var result = await new GameModeEntryTransaction(backend, Custom()).RunAsync(default);

        Assert.Equal(GameModeEntryOutcome.DesktopPreserved, result.Outcome);
        Assert.Equal(SessionModes.ExplorerTakeoverRefusedWarning, result.Warning);
        Assert.DoesNotContain("exit-explorer", backend.Calls);
    }

    [Fact]
    public async Task AnExplorerThatRefusedToLeaveKeepsTheDesktopRatherThanHalfEntering()
    {
        Backend backend = new() { ExitExplorer = false, PreserveDesktop = true };

        var result = await new GameModeEntryTransaction(backend, Custom()).RunAsync(default);

        Assert.Equal(GameModeEntryOutcome.DesktopPreserved, result.Outcome);
        Assert.Equal(SessionModes.ExplorerExitFailedWarning, result.Warning);
        Assert.DoesNotContain("commit", backend.Calls);
    }

    [Fact]
    public async Task ALayoutRefusedAfterExplorerLeftStillEntersAndReportsIt()
    {
        // Past the boundary there is no cheap desktop to go back to. A usable Game Mode on the
        // wrong display beats tearing the whole session down again.
        Backend backend = new() { LayoutOutcome = DisplayLayoutOutcome.Rejected };

        var result = await new GameModeEntryTransaction(backend, Custom()).RunAsync(default);

        Assert.Equal(GameModeEntryOutcome.Entered, result.Outcome);
        Assert.Contains("Game Mode display layout", result.Warning!, StringComparison.Ordinal);
        Assert.Contains("commit", backend.Calls);
    }

    [Fact]
    public async Task ADisplayThatVanishesBeforeTheBoundaryIsWaitedForAgain()
    {
        Backend backend = new() { MissingOnRecheck = true };

        var result = await new GameModeEntryTransaction(backend, Custom()).RunAsync(default);

        Assert.Equal(GameModeEntryOutcome.Entered, result.Outcome);
        Assert.Equal(2, backend.Calls.Count(call => call == "wait"));
    }

    [Fact]
    public async Task TheReturnLayoutIsRecordedBeforeExplorerLeavesAndClearedWhenEntryIsUndone()
    {
        Backend backend = new() { PrepareExplorer = false };

        await new GameModeEntryTransaction(backend, Custom()).RunAsync(default);

        Assert.Equal(["captured", null], backend.PersistedReturns);
    }

    [Fact]
    public async Task AConfiguredDesktopLayoutIsWhatEntryPromisesToRestore()
    {
        Backend backend = new() { PrepareExplorer = false };
        GameModeLaunchConfiguration launch = Custom();
        launch.Return = GameModeReturn.DesktopLayout;
        launch.DesktopLayout = Layout(Desk);

        await new GameModeEntryTransaction(backend, launch).RunAsync(default);

        // The configured layout, not whatever the desktop happened to look like at entry.
        Assert.Equal(["desk"], backend.PersistedReturns.Where(name => name is not null));
        Assert.Equal(["desk"], backend.RestoredLayouts);
    }

    [Fact]
    public async Task TheSplashStopsOfferingCancelExactlyAtTheExplorerExit()
    {
        Backend backend = new();

        await new GameModeEntryTransaction(backend, Custom()).RunAsync(default);

        int lastCancellable = backend.Calls.LastIndexOf("cancellable:true");
        int notCancellable = backend.Calls.IndexOf("cancellable:false");
        int exit = backend.Calls.IndexOf("exit-explorer");
        Assert.InRange(notCancellable, lastCancellable + 1, exit);
    }

    private sealed class Backend : IGameModeEntryBackend
    {
        private int _observations;

        internal List<string> Calls { get; } = [];

        /// <summary>Calls with the splash's button-label changes filtered out, for the tests that
        /// assert the exact order of the work itself.</summary>
        internal IEnumerable<string> Steps =>
            Calls.Where(call => !call.StartsWith("cancellable", StringComparison.Ordinal));

        internal List<string?> PersistedReturns { get; } = [];

        internal List<string> RestoredLayouts { get; } = [];

        internal string? FailAction { get; init; }

        internal Action? OnWait { get; init; }

        internal bool PrepareExplorer { get; init; } = true;

        internal bool ExitExplorer { get; init; } = true;

        internal bool PreserveDesktop { get; init; }

        internal bool MissingOnRecheck { get; init; }

        internal DisplayLayoutOutcome LayoutOutcome { get; init; } = DisplayLayoutOutcome.Applied;

        public void SetStatus(string line) { }

        public void SetCancellable(bool cancellable) => Calls.Add($"cancellable:{(cancellable ? "true" : "false")}");

        public Task<DisplayArrangement> ObserveAsync()
        {
            Calls.Add("observe");
            _observations++;
            bool visible = !MissingOnRecheck || _observations != 2;
            return Task.FromResult(new DisplayArrangement(
                visible ? [new(Tv, true, true, new(Tv, 0, 0, 1920, 1080, DisplayRefresh.FromHertz(60)))] : [],
                "captured", DateTimeOffset.UnixEpoch));
        }

        public Task<DisplayArrangement> WaitForDisplaysAsync(
            IReadOnlyList<DisplayTargetIdentity> targets, CancellationToken cancellationToken)
        {
            Calls.Add("wait");
            OnWait?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return ObserveWithoutRecording();
        }

        public Task<DisplayLayoutResult> ApplyLayoutAsync(
            DisplayLayout layout, CancellationToken cancellationToken)
        {
            string name = layout.Outputs[0].Target.FriendlyName == "Desk" ? "desk" : "captured";
            // A restore is the same call with the layout entry recorded; distinguishing them by
            // whether Explorer has left yet would test the test, so both are recorded.
            Calls.Add("apply-layout");
            RestoredLayouts.Add(name);
            return Task.FromResult(new DisplayLayoutResult(LayoutOutcome, [], 0, false, false, [], "refused"));
        }

        public Task PersistPendingReturnAsync(DisplayLayout? layout)
        {
            Calls.Add(layout is null ? "clear-return" : "persist-return");
            PersistedReturns.Add(layout is null ? null
                : layout.Outputs[0].Target.FriendlyName == "Desk" ? "desk" : "captured");
            return Task.CompletedTask;
        }

        public void ApplyDefaultPosture() => Calls.Add("default-posture");

        public Task<IReadOnlyList<PluginActionStepResult>> RunEnterActionsAsync(
            CancellationToken cancellationToken)
        {
            Calls.Add("enter-actions");
            List<PluginActionStepResult> results = [];
            foreach (PluginActionStep step in new[] { Step("switch-to-pc"), Step("tv-on") })
            {
                bool failed = step.ActionId == FailAction;
                results.Add(new(step,
                    failed ? PluginActionOutcome.Rejected : PluginActionOutcome.Dispatched,
                    failed ? $"{step.ActionId} refused" : "sent"));
                if (failed) { break; }
            }
            return Task.FromResult<IReadOnlyList<PluginActionStepResult>>(results);
        }

        public Task<IReadOnlyList<PluginActionStepResult>> RunLeaveActionsAsync()
        {
            Calls.Add("leave-actions");
            return Task.FromResult<IReadOnlyList<PluginActionStepResult>>([]);
        }

        public Task<bool> PrepareExplorerExitAsync()
        {
            Calls.Add("prepare-explorer");
            return Task.FromResult(PrepareExplorer);
        }

        public Task<bool> ExitExplorerAndWaitAsync()
        {
            Calls.Add("exit-explorer");
            return Task.FromResult(ExitExplorer);
        }

        public Task<bool> MustPreserveDesktopAsync() => Task.FromResult(PreserveDesktop);

        public Task<string?> RequestBigPictureAsync()
        {
            Calls.Add("big-picture");
            return Task.FromResult<string?>(null);
        }

        public void ExitBigPicture() => Calls.Add("exit-big-picture");

        public void CommitGameMode() => Calls.Add("commit");

        private Task<DisplayArrangement> ObserveWithoutRecording() =>
            Task.FromResult(new DisplayArrangement(
                [new(Tv, true, true, new(Tv, 0, 0, 1920, 1080, DisplayRefresh.FromHertz(60)))],
                "captured", DateTimeOffset.UnixEpoch));
    }
}
