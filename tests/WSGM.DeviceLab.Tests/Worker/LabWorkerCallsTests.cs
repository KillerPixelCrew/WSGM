using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Tests.Worker;

public sealed class LabWorkerCallsTests
{
    [Fact]
    public void Cancel_CancelsTheRunningCall()
    {
        LabWorkerCalls calls = new();
        var call = calls.Begin(3);

        calls.Cancel(3);

        Assert.True(call.Token.IsCancellationRequested);
        calls.End(call);
    }

    [Fact]
    public void Cancel_ForACallNotStartedYet_CancelsItWhenItStarts()
    {
        LabWorkerCalls calls = new();

        calls.Cancel(4);
        var call = calls.Begin(4);

        Assert.True(call.Token.IsCancellationRequested);
        calls.End(call);
    }

    [Fact]
    public void Cancel_ForAFinishedCall_DoesNotTouchLaterCalls()
    {
        LabWorkerCalls calls = new();
        calls.End(calls.Begin(5));

        calls.Cancel(5);
        var call = calls.Begin(6);

        Assert.False(call.Token.IsCancellationRequested);
        calls.End(call);
    }

    [Fact]
    public void CancelAll_CancelsTheRunningCallAndEveryLaterOne()
    {
        LabWorkerCalls calls = new();
        var running = calls.Begin(7);

        calls.CancelAll();
        var later = calls.Begin(8);

        Assert.True(running.Token.IsCancellationRequested);
        Assert.True(later.Token.IsCancellationRequested);
        calls.End(running);
        calls.End(later);
    }
}
