using WSGM.Core;

namespace WSGM.Tests;

public sealed class LaunchWrapperCommandTests
{
    private const string Helper = "C:\\Users\\Player One\\WSGM.Launch.exe";

    [Theory]
    [InlineData(LaunchWrapperMode.Deelevate, "--deelevate")]
    [InlineData(LaunchWrapperMode.InputLease, "--input-lease")]
    [InlineData(LaunchWrapperMode.Both, "--deelevate --input-lease")]
    [InlineData(LaunchWrapperMode.InputLeaseInject, "--input-lease-inject")]
    [InlineData(LaunchWrapperMode.BothInject, "--deelevate --input-lease-inject")]
    public void SteamLaunchOptionsWrapTheHelperAndPreserveTheOriginalCommandPlaceholder(
        LaunchWrapperMode mode, string expectedFlags)
        => Assert.Equal(
            $"\"{Helper}\" {expectedFlags} -- %command%",
            LaunchWrapperCommand.SteamLaunchOptions(Helper, mode));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SteamLaunchOptionsRejectAMissingHelperPath(string helperPath)
        => Assert.Throws<ArgumentException>(
            () => LaunchWrapperCommand.SteamLaunchOptions(helperPath, LaunchWrapperMode.Deelevate));

    [Fact]
    public void SteamLaunchOptionsRejectAModeWithNoBehaviour()
        => Assert.Throws<ArgumentException>(
            () => LaunchWrapperCommand.SteamLaunchOptions(Helper, LaunchWrapperMode.None));

    // A title's existing launch options must survive being wrapped: %command%
    // expands to the game's own command only, so options replaced by the wrapper
    // value would silently stop applying. (Real titles only — a non-Steam shortcut
    // ignores %command% entirely and takes the wrapper in its Target instead.)
    [Fact]
    public void SteamLaunchOptionsAppendPlainOriginalOptionsAfterThePlaceholder()
        => Assert.Equal(
            $"\"{Helper}\" --deelevate -- %command% -dx11 -nolauncher",
            LaunchWrapperCommand.SteamLaunchOptions(
                Helper, LaunchWrapperMode.Deelevate, "-dx11 -nolauncher"));

    [Fact]
    public void SteamLaunchOptionsSubstituteTheWrapperIntoAUserPlacedPlaceholder()
        => Assert.Equal(
            $"profiler.exe \"{Helper}\" --input-lease -- %command% -windowed",
            LaunchWrapperCommand.SteamLaunchOptions(
                Helper, LaunchWrapperMode.InputLease, "profiler.exe %command% -windowed"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SteamLaunchOptionsWithoutOriginalsAreTheBareWrapperCommand(string? original)
        => Assert.Equal(
            $"\"{Helper}\" --deelevate -- %command%",
            LaunchWrapperCommand.SteamLaunchOptions(Helper, LaunchWrapperMode.Deelevate, original));

    [Theory]
    [InlineData("-dx11 -nolauncher")]
    [InlineData("profiler.exe %command% -windowed")]
    [InlineData("")]
    public void OriginalLaunchOptionsRoundTripThroughTheWrappedValue(string original)
    {
        var wrapped = LaunchWrapperCommand.SteamLaunchOptions(
            Helper, LaunchWrapperMode.Both, original);

        Assert.Equal(original, LaunchWrapperCommand.OriginalLaunchOptions(wrapped));
    }

    [Fact]
    public void ReapplyingADifferentModeKeepsTheOriginalOptionsAndDoesNotNestTheWrapper()
    {
        var first = LaunchWrapperCommand.SteamLaunchOptions(
            Helper, LaunchWrapperMode.Deelevate, "-dx11");

        var second = LaunchWrapperCommand.SteamLaunchOptions(
            Helper, LaunchWrapperMode.Both, LaunchWrapperCommand.OriginalLaunchOptions(first));

        Assert.Equal($"\"{Helper}\" --deelevate --input-lease -- %command% -dx11", second);
        Assert.DoesNotContain("-- %command% \"", second, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginalLaunchOptionsLeaveAnUnwrappedValueAlone()
        => Assert.Equal("-dx11", LaunchWrapperCommand.OriginalLaunchOptions("  -dx11  "));

    // A game can be wrapped without WSGM holding a snapshot — the user pasted the
    // copied command, or the configuration was reset. Snapshotting the values on
    // screen would record the wrapper as the "original" and make Remove restore it.
    [Fact]
    public void OriginalsFromUnwrapAWrappedTitleSoTheSnapshotIsTheUsersOwnOptions()
    {
        var wrapped = LaunchWrapperCommand.SteamLaunchOptions(
            Helper, LaunchWrapperMode.Both, "-dx11");
        var details = new SteamLaunchDetails(wrapped, "", "", "");

        var originals = SteamLaunchConfig.OriginalsFrom(isShortcut: false, details);

        Assert.Equal("-dx11", originals.LaunchOptions);
    }

    [Fact]
    public void OriginalsFromUnwrapAWrappedShortcutBackToItsRealProgram()
    {
        var details = new SteamLaunchDetails(
            "",
            LaunchWrapperCommand.ShortcutTarget(Helper),
            LaunchWrapperCommand.ShortcutArguments(
                LaunchWrapperMode.Deelevate, "\"D:\\Games\\game.exe\"", "-windowed"),
            "D:\\Games");

        var originals = SteamLaunchConfig.OriginalsFrom(isShortcut: true, details);

        Assert.Equal("\"D:\\Games\\game.exe\"", originals.Target);
        Assert.Equal("-windowed", originals.LaunchOptions);
        Assert.Equal("D:\\Games", originals.StartDir);
    }

    [Fact]
    public void OriginalsFromLeaveAnUnwrappedGameUntouched()
    {
        var details = new SteamLaunchDetails("-dx11", "\"D:\\g\\game.exe\"", "-mod", "D:\\g");

        var originals = SteamLaunchConfig.OriginalsFrom(isShortcut: false, details);

        Assert.Equal("\"D:\\g\\game.exe\"", originals.Target);
        Assert.Equal("-dx11", originals.LaunchOptions);
    }

    // Steam stores a shortcut's Target verbatim and its own shortcuts carry the
    // quoted form, so the quotes are part of the value WSGM has to write.
    [Fact]
    public void ShortcutTargetIsQuotedForPathsContainingSpaces()
        => Assert.Equal($"\"{Helper}\"", LaunchWrapperCommand.ShortcutTarget(Helper));

    [Fact]
    public void ShortcutArgumentsKeepSteamsAlreadyQuotedTargetUnchanged()
        => Assert.Equal(
            "--deelevate -- \"C:\\Games\\The Movies\\MoviesSE.exe\"",
            LaunchWrapperCommand.ShortcutArguments(
                LaunchWrapperMode.Deelevate, "\"C:\\Games\\The Movies\\MoviesSE.exe\"", null));

    [Fact]
    public void ShortcutArgumentsQuoteABareTarget()
        => Assert.Equal(
            "--input-lease -- \"C:\\Games\\The Movies\\MoviesSE.exe\"",
            LaunchWrapperCommand.ShortcutArguments(
                LaunchWrapperMode.InputLease, "C:\\Games\\The Movies\\MoviesSE.exe", ""));

    [Fact]
    public void ShortcutArgumentsPreserveTheShortcutsOwnArguments()
        => Assert.Equal(
            "--deelevate --input-lease -- \"C:\\Games\\game.exe\" -windowed -skipintro",
            LaunchWrapperCommand.ShortcutArguments(
                LaunchWrapperMode.Both, "\"C:\\Games\\game.exe\"", " -windowed -skipintro "));

    [Fact]
    public void ShortcutArgumentsRejectAMissingOriginalTarget()
        => Assert.Throws<ArgumentException>(
            () => LaunchWrapperCommand.ShortcutArguments(LaunchWrapperMode.Both, "  ", null));

    [Theory]
    [InlineData(LaunchWrapperMode.Deelevate)]
    [InlineData(LaunchWrapperMode.InputLease)]
    [InlineData(LaunchWrapperMode.Both)]
    [InlineData(LaunchWrapperMode.InputLeaseInject)]
    [InlineData(LaunchWrapperMode.BothInject)]
    public void ModeForReadsBackEveryBehaviourSteamLaunchOptionsCanWrite(LaunchWrapperMode mode)
        => Assert.Equal(
            mode, LaunchWrapperCommand.ModeFor(LaunchWrapperCommand.SteamLaunchOptions(Helper, mode)));

    // A user's own launch options must never be mistaken for WSGM's, even when
    // they happen to contain the same words.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-novid -high")]
    [InlineData("\"C:\\Other\\tool.exe\" --deelevate --input-lease -- %command%")]
    public void ModeForReportsNoneWithoutTheWrapper(string? value)
        => Assert.Equal(LaunchWrapperMode.None, LaunchWrapperCommand.ModeFor(value));

    [Fact]
    public void TargetsHelperDetectsAShortcutWsgmAlreadyOwns()
    {
        Assert.True(LaunchWrapperCommand.TargetsHelper($"\"{Helper}\""));
        Assert.False(LaunchWrapperCommand.TargetsHelper("\"C:\\Games\\game.exe\""));
        Assert.False(LaunchWrapperCommand.TargetsHelper(null));
    }

    // Removing the wrapper from a shortcut has to recover the program it really
    // runs, which by then lives only inside the arguments WSGM generated.
    [Theory]
    [InlineData(
        "--deelevate -- \"C:\\Games\\The Movies\\MoviesSE.exe\"",
        "\"C:\\Games\\The Movies\\MoviesSE.exe\"", "")]
    [InlineData(
        "--deelevate --input-lease -- \"C:\\Games\\game.exe\" -windowed -skipintro",
        "\"C:\\Games\\game.exe\"", "-windowed -skipintro")]
    [InlineData("--input-lease -- C:\\Games\\bare.exe", "C:\\Games\\bare.exe", "")]
    [InlineData("--input-lease -- C:\\bare.exe -x", "C:\\bare.exe", "-x")]
    public void OriginalFromWrappedArgumentsRecoversTheRealProgram(
        string arguments, string expectedTarget, string expectedArguments)
    {
        var (target, rest) = SteamLaunchConfig.OriginalFromWrappedArguments(arguments);
        Assert.Equal(expectedTarget, target);
        Assert.Equal(expectedArguments, rest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--deelevate")]
    [InlineData("--deelevate -- ")]
    public void OriginalFromWrappedArgumentsReportsNothingWhenThereIsNoWrappedCommand(string? arguments)
    {
        var (target, rest) = SteamLaunchConfig.OriginalFromWrappedArguments(arguments);
        Assert.Equal("", target);
        Assert.Equal("", rest);
    }

    // What ShortcutArguments writes must be exactly what the remover reads back.
    [Fact]
    public void ShortcutArgumentsAndOriginalRecoveryRoundTrip()
    {
        const string original = "\"C:\\Games\\The Movies\\MoviesSE.exe\"";
        const string extra = "-windowed";
        var written = LaunchWrapperCommand.ShortcutArguments(LaunchWrapperMode.Both, original, extra);

        var (target, rest) = SteamLaunchConfig.OriginalFromWrappedArguments(written);

        Assert.Equal(original, target);
        Assert.Equal(extra, rest);
    }

    /// <summary>The substring trap: "--input-lease-inject" contains "--input-lease",
    /// so a plain Contains would report both lease behaviours at once - which then
    /// trips the mutual-exclusion guard the next time the game is re-applied.</summary>
    [Fact]
    public void ModeForDoesNotReadInputLeaseOutOfInputLeaseInject()
    {
        var wrapped = LaunchWrapperCommand.SteamLaunchOptions(
            Helper, LaunchWrapperMode.InputLeaseInject);

        var mode = LaunchWrapperCommand.ModeFor(wrapped);

        Assert.Equal(LaunchWrapperMode.InputLeaseInject, mode);
        Assert.False(mode.HasFlag(LaunchWrapperMode.InputLease));
    }

    [Fact]
    public void OriginalLaunchOptionsRoundTripsAcrossTheLeaseFlagSplit()
    {
        const string user = "-novid -high";
        var shim = LaunchWrapperCommand.SteamLaunchOptions(
            Helper, LaunchWrapperMode.InputLease, user);
        var unwrapped = LaunchWrapperCommand.OriginalLaunchOptions(shim);
        var injected = LaunchWrapperCommand.SteamLaunchOptions(
            Helper, LaunchWrapperMode.InputLeaseInject, unwrapped);

        Assert.Equal(user, unwrapped);
        Assert.Equal(user, LaunchWrapperCommand.OriginalLaunchOptions(injected));
        // Re-applying must never nest one wrapper inside the other.
        Assert.Equal(1, CountOccurrences(injected, "WSGM.Launch.exe"));
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        for (var i = value.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
             i >= 0;
             i = value.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }
        return count;
    }

    [Theory]
    [InlineData(LaunchWrapperMode.InputLease, true, LaunchWrapperMode.InputLease)]
    [InlineData(LaunchWrapperMode.InputLease, false, LaunchWrapperMode.InputLeaseInject)]
    [InlineData(LaunchWrapperMode.Both, false, LaunchWrapperMode.BothInject)]
    [InlineData(LaunchWrapperMode.Deelevate, false, LaunchWrapperMode.Deelevate)]
    [InlineData(LaunchWrapperMode.None, false, LaunchWrapperMode.None)]
    public void ForCurrentInputModeSwapsOnlyTheLeaseBit(
        LaunchWrapperMode requested, bool shimManaged, LaunchWrapperMode expected)
        => Assert.Equal(
            expected, LaunchWrapperCommand.ForCurrentInputMode(requested, shimManaged));

    [Fact]
    public void SteamLaunchOptionsRefusesToAskForBothLeaseBehavioursAtOnce()
        => Assert.Throws<ArgumentException>(() => LaunchWrapperCommand.SteamLaunchOptions(
            Helper, LaunchWrapperMode.InputLease | LaunchWrapperMode.InputLeaseInject));

    // Pins the log-only prefix reporter added for launch-option
    // diagnosability. Nothing here may change what SteamLaunchOptions emits: Steam
    // stores that value verbatim, and a user-placed prefix ahead of %command% is
    // deliberately preserved rather than stripped (docs\steam-cef.md invariant 11).
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
