using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class ProcessEfficiencyTests
{
    private readonly List<bool> _writes = [];
    private readonly List<string> _warnings = [];

    private ProcessEfficiency Create()
    {
        return new ProcessEfficiency(_writes.Add, static _ => { }, _warnings.Add);
    }

    [Fact]
    public void WritesNothingUntilStarted()
    {
        var efficiency = Create();

        efficiency.SurfaceOpened("sheet");
        efficiency.SurfaceClosed("sheet");

        Assert.Empty(_writes);
    }

    [Fact]
    public void StartSelectsEcoWhenNoWindowIsOnScreen()
    {
        var efficiency = Create();

        efficiency.Start();

        Assert.Equal([true], _writes);
    }

    [Fact]
    public void StartSelectsHighWhenAWindowOpenedDuringBoot()
    {
        var efficiency = Create();
        efficiency.SurfaceOpened("sheet");

        efficiency.Start();

        Assert.Equal([false], _writes);
    }

    [Fact]
    public void OnlyTheLastWindowClosingReturnsToEco()
    {
        var efficiency = Create();
        efficiency.Start();

        efficiency.SurfaceOpened("sheet");
        efficiency.SurfaceOpened("settings");
        efficiency.SurfaceClosed("sheet");
        efficiency.SurfaceClosed("settings");

        Assert.Equal([true, false, true], _writes);
    }

    [Fact]
    public void RepeatedEventsWriteOnce()
    {
        var efficiency = Create();
        efficiency.Start();
        efficiency.Start();

        efficiency.SurfaceOpened("sheet");
        efficiency.SurfaceOpened("sheet");
        efficiency.SurfaceClosed("sheet");
        efficiency.SurfaceClosed("sheet");

        Assert.Equal([true, false, true], _writes);
    }

    [Fact]
    public void ARefusedWriteIsReportedAndNotRetried()
    {
        var attempts = 0;
        var efficiency = new ProcessEfficiency(
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("refused");
            },
            static _ => { },
            _warnings.Add);

        efficiency.Start();
        efficiency.Start();

        Assert.Equal(expected: 1, attempts);
        Assert.Single(_warnings);
    }
}
