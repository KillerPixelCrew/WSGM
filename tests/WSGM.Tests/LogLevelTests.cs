using WSGM.Core;

namespace WSGM.Tests;

/// <summary>
/// Log is a static shared by the whole process, so these run in one collection and restore the
/// default threshold. They assert the threshold itself; the file write cannot be observed here
/// because Log stays uninitialized in tests.
/// </summary>
[Collection("log-level")]
public sealed class LogLevelTests : IDisposable
{
    public void Dispose() => Log.SetVerbosity(LogVerbosity.Normal);

    [Fact]
    public void TheDefaultKeepsEverythingExceptDebug()
    {
        Log.SetVerbosity(LogVerbosity.Normal);

        Assert.Equal(LogLevel.Info, Log.MinimumLevel);
        Assert.True(LogLevel.Debug < Log.MinimumLevel);
        Assert.True(LogLevel.Info >= Log.MinimumLevel);
        Assert.True(LogLevel.Warn >= Log.MinimumLevel);
        Assert.True(LogLevel.Error >= Log.MinimumLevel);
    }

    [Fact]
    public void VerboseAddsDebugAndNothingElse()
    {
        Log.SetVerbosity(LogVerbosity.Verbose);

        Assert.Equal(LogLevel.Debug, Log.MinimumLevel);
        Assert.True(LogLevel.Debug >= Log.MinimumLevel);
    }

    [Fact]
    public void TheLevelOrderIsLowestFirstSoTheThresholdComparisonHolds()
    {
        // The threshold is a `level < minimum` test, so this ordering is load-bearing rather than
        // cosmetic: reordering the enum would silently start dropping errors.
        Assert.True(LogLevel.Debug < LogLevel.Info);
        Assert.True(LogLevel.Info < LogLevel.Warn);
        Assert.True(LogLevel.Warn < LogLevel.Error);
    }

    [Fact]
    public void NoLevelCanEverSuppressAFailure()
    {
        foreach (LogVerbosity verbosity in Enum.GetValues<LogVerbosity>())
        {
            Log.SetVerbosity(verbosity);
            Assert.True(LogLevel.Error >= Log.MinimumLevel);
            Assert.True(LogLevel.Warn >= Log.MinimumLevel);
        }
    }

    [Theory]
    [InlineData(true, "--verbose")]
    [InlineData(true, "--shell", "--verbose")]
    [InlineData(true, "--VERBOSE")]
    [InlineData(false, "--shell")]
    [InlineData(false)]
    public void TheVerboseFlagIsRecognizedAnywhereAndCaseInsensitively(
        bool expected,
        params string[] args) =>
        Assert.Equal(expected, Program.HasVerboseFlag(args));
}
