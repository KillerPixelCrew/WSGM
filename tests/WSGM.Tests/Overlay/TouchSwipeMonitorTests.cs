using WSGM.Core;
using WSGM.Overlay;

namespace WSGM.Tests.Overlay;

/// <summary>Synthetic contact sequences through the monitor's report handling, with the UI-thread post captured.</summary>
public sealed class TouchSwipeMonitorTests
{
    private static readonly TouchDigitizer Panel = new() { Usable = true, XMin = 0, XMax = 1279, YMin = 0, YMax = 799 };

    [Fact]
    public void ARecognizedBottomSwipeOpensOnlyWhenTheFingerLifts()
    {
        var (report, posts, triggered) = Monitor();

        SwipeUpFromTheBottom(report);
        Assert.Empty(posts);

        report(new TouchContactReport(Panel, Contacts: 1, TipDown: false, RawX: 0, RawY: 0));
        RunAll(posts);

        Assert.Equal([ScreenEdge.Bottom], triggered);
    }

    [Fact]
    public void ASecondContactCancelsARecognizedSwipeBeforeItsLift()
    {
        var (report, posts, triggered) = Monitor();

        SwipeUpFromTheBottom(report);
        report(new TouchContactReport(Panel, Contacts: 2, TipDown: true, RawX: 643, RawY: 739));
        report(new TouchContactReport(Panel, Contacts: 1, TipDown: false, RawX: 0, RawY: 0));
        RunAll(posts);

        Assert.Empty(triggered);
    }

    private static (Action<TouchContactReport> Report, List<Action> Posts, List<ScreenEdge> Triggered) Monitor()
    {
        Action<TouchContactReport>? handler = null;
        var posts = new List<Action>();
        var triggered = new List<ScreenEdge>();
        var monitor = new TouchSwipeMonitor(subscribe =>
        {
            handler = subscribe;
            return new Subscription();
        }, () => (1280, 800), posts.Add);
        monitor.Configure(new GestureConfig { BottomEdgeTaskbar = true });
        monitor.Triggered += triggered.Add;
        return (handler!, posts, triggered);
    }

    private static void SwipeUpFromTheBottom(Action<TouchContactReport> report)
    {
        report(new TouchContactReport(Panel, Contacts: 1, TipDown: true, RawX: 640, RawY: 798));
        report(new TouchContactReport(Panel, Contacts: 1, TipDown: true, RawX: 641, RawY: 781));
        report(new TouchContactReport(Panel, Contacts: 1, TipDown: true, RawX: 643, RawY: 739));
    }

    private static void RunAll(List<Action> posts)
    {
        foreach (var post in posts.ToArray())
        {
            post();
        }
    }

    private sealed class Subscription : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
