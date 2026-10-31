using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class EndpointSelectionTrackerTests
{
    [Fact]
    public void RapidEndpointSelectionsFinishWithTheLatestChoice()
    {
        // The revision bookkeeping behind rapid default-endpoint changes: a stale
        // selection may neither publish UI state nor settle the flow — only the
        // newest revision does. (The writes themselves are serialized by the
        // per-flow gate in AudioEndpointSelection.)
        var tracker = default(EndpointSelectionTracker);
        Assert.False(tracker.Pending);

        var first = tracker.Begin();
        Assert.True(tracker.Pending);
        Assert.True(tracker.IsCurrent(first));

        var second = tracker.Begin();
        Assert.False(tracker.IsCurrent(first));
        Assert.True(tracker.IsCurrent(second));

        tracker.Complete(first);
        Assert.True(tracker.Pending);

        tracker.Complete(second);
        Assert.False(tracker.Pending);
        Assert.True(tracker.IsCurrent(second));
    }
}
