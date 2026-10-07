extern alias packagedLaunch;
using packagedLaunch::WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

public sealed class LoadAttemptResultsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedLoadsReuseTheActualOutcome(bool succeeded)
    {
        var results = new LoadAttemptResults();
        var calls = 0;

        bool Load()
        {
            calls++;
            return succeeded;
        }

        Assert.Equal(succeeded, results.Load(@"123|C:\Game\Steam.dll", false, Load));
        Assert.Equal(succeeded, results.Load(@"123|c:\game\steam.dll", false, Load));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AnInterruptedLoadCannotBeDispatchedAgain()
    {
        var results = new LoadAttemptResults();
        Assert.Throws<InvalidOperationException>(() => results.Load("123|steam.dll", false,
            () => throw new InvalidOperationException()));
        Assert.False(results.Load("123|steam.dll", false, () => throw new Exception("Repeated load")));
    }

    [Fact]
    public void DifferentProcessesAndComponentsHaveTheirOwnOutcome()
    {
        var results = new LoadAttemptResults();
        Assert.False(results.Load("123|steam.dll", false, () => false));
        Assert.True(results.Load("124|steam.dll", false, () => true));
        Assert.True(results.Load("123|overlay.dll", false, () => true));
    }

    [Fact]
    public void AProcessLatchOverridesSuccessAndPreventsNewDispatch()
    {
        var results = new LoadAttemptResults();
        Assert.True(results.Load("123|steam.dll", false, () => true));
        Assert.False(results.Load("123|steam.dll", true, () => throw new Exception("Repeated load")));
        Assert.False(results.Load("123|overlay.dll", true, () => throw new Exception("Latched load")));
    }
}
