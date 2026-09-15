using WSGM.Core;

namespace WSGM.Tests;

/// <summary>Pins the log-only prefix reporter added for launch-option
/// diagnosability. Nothing here may change what SteamLaunchOptions emits: Steam
/// stores that value verbatim, and a user-placed prefix ahead of %command% is
/// deliberately preserved rather than stripped (docs\steam-cef.md invariant 11).</summary>
public sealed class LaunchPrefixTests
{
    private const string Helper = @"C:\Users\Player One\WSGM.Launch.exe";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PreservedPrefix_BlankOptions_IsEmpty(string? original)
        => Assert.Equal("", LaunchWrapperCommand.PreservedPrefix(original));

    [Fact]
    public void PreservedPrefix_OptionsWithoutThePlaceholder_IsEmpty()
        => Assert.Equal("", LaunchWrapperCommand.PreservedPrefix("-dx11 -nolauncher"));

    [Fact]
    public void PreservedPrefix_PlaceholderAtTheStart_IsEmpty()
        => Assert.Equal("", LaunchWrapperCommand.PreservedPrefix("%command% -windowed"));

    [Fact]
    public void PreservedPrefix_ShimAheadOfThePlaceholder_IsTheShim()
        => Assert.Equal(
            "profiler.exe",
            LaunchWrapperCommand.PreservedPrefix("profiler.exe %command% -windowed"));

    [Fact]
    public void PreservedPrefix_ShimWithItsOwnArguments_KeepsThoseArguments()
        => Assert.Equal(
            @"""C:\Tools\rtss.exe"" --hook --profile=default",
            LaunchWrapperCommand.PreservedPrefix(
                @"""C:\Tools\rtss.exe"" --hook --profile=default %command% -dx11"));

    // Log.Write interpolates its message raw, so an options value carrying a newline
    // could otherwise forge whole lines in wsgm.log — the only remote-diagnosis
    // surface WSGM has.
    [Fact]
    public void PreservedPrefix_ControlCharactersInThePrefix_AreRemoved()
        => Assert.Equal(
            "profiler.exe2026-01-01 [Info] forged",
            LaunchWrapperCommand.PreservedPrefix(
                "profiler.exe\r\n2026-01-01 [Info] forged\t %command%"));

    [Fact]
    public void PreservedPrefix_PrefixOfOnlyControlCharacters_IsEmpty()
        => Assert.Equal("", LaunchWrapperCommand.PreservedPrefix("\u0001\u0002 %command%"));

    [Fact]
    public void PreservedPrefix_PrefixLongerThanTheCap_IsTruncatedAndMarked()
    {
        var prefix = LaunchWrapperCommand.PreservedPrefix(new string('a', 300) + " %command%");

        Assert.Equal(new string('a', 200) + "...", prefix);
    }

    [Fact]
    public void PreservedPrefix_PrefixExactlyAtTheCap_IsNotMarked()
    {
        var prefix = LaunchWrapperCommand.PreservedPrefix(new string('a', 200) + " %command%");

        Assert.Equal(new string('a', 200), prefix);
    }

    // The reporter is diagnostics only. What Steam is handed must stay byte-identical
    // to what it was before the log line existed, prefix and all.
    [Fact]
    public void PreservedPrefix_ReportingAPrefix_DoesNotChangeTheEmittedLaunchOptions()
    {
        const string original = "profiler.exe %command% -windowed";

        var emitted = LaunchWrapperCommand.SteamLaunchOptions(
            Helper, LaunchWrapperMode.InputLease, original);

        Assert.Equal("profiler.exe", LaunchWrapperCommand.PreservedPrefix(original));
        Assert.Equal(
            $"profiler.exe \"{Helper}\" --input-lease -- %command% -windowed", emitted);
    }
}
