using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DesktopReturnSequenceTests
{
    [Theory]
    [InlineData("steam")]
    [InlineData("layout")]
    [InlineData("retire")]
    [InlineData("leave")]
    [InlineData("clear")]
    public async Task AnOptionalFailureCannotSkipExplorerRecovery(string fail)
    {
        Backend backend = new() { Fail = fail };
        List<string> errors = [];
        Assert.True(await DesktopReturnSequence.RunAsync(backend, true, (phase, _) => errors.Add(phase)));
        Assert.Single(errors);
        Assert.Contains("explorer", backend.Calls);
        Assert.True(backend.Calls.IndexOf("explorer") < backend.Calls.IndexOf("leave"));
        Assert.Equal(fail != "layout", backend.Calls.Contains("clear"));
    }

    [Fact]
    public async Task FailedDesktopRetainsTheRecoveryRecordAndDoesNotSwitchAway()
    {
        Backend backend = new() { DesktopReady = false };
        Assert.False(await DesktopReturnSequence.RunAsync(backend, true, (_, _) => { }));
        Assert.Equal(["steam", "layout", "retire", "explorer"], backend.Calls);
    }

    [Fact]
    public async Task FailedLayoutDoesNotClearWhatTheSessionOwesTheDesktop()
    {
        Backend backend = new() { LayoutReady = false };
        Assert.True(await DesktopReturnSequence.RunAsync(backend, false, (_, _) => { }));
        Assert.Equal(["steam", "layout", "retire", "explorer"], backend.Calls);
    }

    [Fact]
    public async Task SlowLeaveActionsBeginOnlyAfterTheDesktopAndRecoveryRecordHaveSettled()
    {
        TaskCompletionSource leave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Backend backend = new() { LeaveGate = leave.Task };
        var returning = DesktopReturnSequence.RunAsync(backend, true, (_, _) => { });
        Assert.False(returning.IsCompleted);
        Assert.Equal(["steam", "layout", "retire", "explorer", "clear", "leave"], backend.Calls);
        leave.SetResult();
        Assert.True(await returning);
    }

    private sealed class Backend : IDesktopReturnBackend
    {
        internal List<string> Calls { get; } = [];
        internal string? Fail { get; init; }
        internal bool DesktopReady { get; init; } = true;
        internal bool LayoutReady { get; init; } = true;
        internal Task LeaveGate { get; init; } = Task.CompletedTask;

        public Task ExitBigPictureAsync()
        {
            return Call("steam");
        }

        public async Task<bool> RestoreLayoutAsync()
        {
            await Call("layout");
            return LayoutReady;
        }

        public Task RetireGameModeAsync()
        {
            return Call("retire");
        }

        public async Task<bool> RestoreExplorerAsync()
        {
            await Call("explorer");
            return DesktopReady;
        }

        public async Task RunLeaveActionsAsync()
        {
            await Call("leave");
            await LeaveGate;
        }

        public Task ClearPendingReturnAsync()
        {
            return Call("clear");
        }

        private Task Call(string name)
        {
            Calls.Add(name);
            return name == Fail ? Task.FromException(new InvalidOperationException(name)) : Task.CompletedTask;
        }
    }
}
