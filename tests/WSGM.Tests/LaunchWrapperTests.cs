using WSGM.Core;
using WSGM.Launch;

namespace WSGM.Tests;

public sealed class LaunchWrapperTests
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

    // The elevated parent recognizes this exact marker in the child's failure
    // message to fail open when de-elevation is impossible (UAC switched off).
    [Fact]
    public void TheDisabledUacFailureMessageCarriesTheMarkerTheParentMatches()
        => Assert.Contains(
            WSGM.Launch.Program.NoMediumTokenMarker,
            WSGM.Launch.Program.DisabledUacFailureMessage,
            StringComparison.Ordinal);

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
    public void ModeForReadsBackWhatSteamLaunchOptionsWrote(LaunchWrapperMode mode)
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

    [Fact]
    public async Task LaunchPayloadRoundTripsArgumentsEnvironmentAndWorkingDirectory()
    {
        var expected = new LaunchPayload(
            "C:\\Games\\Emulator",
            ["C:\\Games\\Emulator\\Ryujinx.exe", "--fullscreen", "value with spaces", "雪"],
            [KeyValuePair.Create("SteamAppId", "1234"), KeyValuePair.Create("EMPTY", "")]);
        await using var stream = new MemoryStream();

        await expected.WriteAsync(stream, CancellationToken.None);
        stream.Position = 0;
        var actual = await LaunchPayload.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
        Assert.Equal(expected.Arguments, actual.Arguments);
        Assert.Equal(expected.EnvironmentVariables, actual.EnvironmentVariables);
    }

    [Fact]
    public void ScheduledTaskUsesInteractiveTokenWithoutAnElevatedRunLevel()
    {
        var xml = ScheduledTaskLauncher.BuildTaskXml(
            "C:\\A&B\\WSGM.Launch.exe", "pipe<name>");

        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.DoesNotContain("<RunLevel>", xml);
        Assert.Contains("<Command>C:\\A&amp;B\\WSGM.Launch.exe</Command>", xml);
        Assert.Contains("<Arguments>--medium-child pipe&lt;name&gt;</Arguments>", xml);
    }

    [Theory]
    [InlineData(new[] { "--deelevate", "--", "C:\\game.exe", "-x" }, true, false)]
    [InlineData(new[] { "--input-lease", "--", "C:\\game.exe" }, false, true)]
    [InlineData(new[] { "--deelevate", "--input-lease", "--", "C:\\game.exe" }, true, true)]
    public void CommandLineSplitsBehaviourFlagsFromTheWrappedCommand(
        string[] arguments, bool deelevate, bool inputLease)
    {
        Assert.True(CommandLine.TryParse(arguments, out var options, out var error));
        Assert.Null(error);
        Assert.Equal(deelevate, options.Deelevate);
        Assert.Equal(inputLease, options.InputLease);
        Assert.Equal(arguments[Array.IndexOf(arguments, "--") + 1], options.Command[0]);
    }

    // Steam expands %command% into several arguments; re-quoting them here would
    // corrupt any path containing a space.
    [Fact]
    public void CommandLinePreservesWrappedArgumentsIndividually()
    {
        Assert.True(CommandLine.TryParse(
            ["--deelevate", "--", "C:\\Program Files\\game.exe", "-map", "de dust"],
            out var options,
            out _));
        Assert.Equal(["C:\\Program Files\\game.exe", "-map", "de dust"], options.Command);
    }

    // Flags after -- belong to the game, not the wrapper.
    [Fact]
    public void CommandLineDoesNotReadWrapperFlagsOutOfTheWrappedCommand()
    {
        Assert.True(CommandLine.TryParse(
            ["--deelevate", "--", "C:\\game.exe", "--input-lease"], out var options, out _));
        Assert.False(options.InputLease);
        Assert.Equal(["C:\\game.exe", "--input-lease"], options.Command);
    }

    // Cast to object: a lone string[] would otherwise be spread as the params array
    // instead of being passed as the single argument.
    [Theory]
    [InlineData((object)new[] { "--deelevate" })]
    [InlineData((object)new[] { "--deelevate", "--" })]
    [InlineData((object)new[] { "--", "C:\\game.exe" })]
    [InlineData((object)new[] { "--bogus", "--", "C:\\game.exe" })]
    [InlineData((object)new[] { "--target-name" })]
    public void CommandLineRejectsIncompleteInvocations(string[] arguments)
    {
        Assert.False(CommandLine.TryParse(arguments, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void CommandLineAllowsDiagnosticsWithoutACommand()
    {
        Assert.True(CommandLine.TryParse(["--status"], out var status, out _));
        Assert.True(status.Status);
        Assert.True(CommandLine.TryParse(["--rescan"], out var rescan, out _));
        Assert.True(rescan.Rescan);
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

    [Theory]
    [InlineData(LaunchWrapperMode.InputLease)]
    [InlineData(LaunchWrapperMode.InputLeaseInject)]
    [InlineData(LaunchWrapperMode.BothInject)]
    public void ModeForReadsBackEveryLeaseBehaviourSteamLaunchOptionsCanWrite(
        LaunchWrapperMode mode)
        => Assert.Equal(
            mode,
            LaunchWrapperCommand.ModeFor(LaunchWrapperCommand.SteamLaunchOptions(Helper, mode)));

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

    [Fact]
    public void CommandLineAcceptsInputLeaseInjectAsTheOnlyBehaviour()
    {
        Assert.True(CommandLine.TryParse(
            ["--input-lease-inject", "--", "game.exe"], out var options, out _));

        Assert.True(options.InputLeaseInject);
        Assert.False(options.InputLease);
        Assert.True(options.AnyLease);
    }

    [Fact]
    public void CommandLineRejectsBothLeaseFlagsTogether()
    {
        Assert.False(CommandLine.TryParse(
            ["--input-lease", "--input-lease-inject", "--", "game.exe"], out _, out var error));

        Assert.Contains("mutually exclusive", error);
    }

    // The failure text arrives over an unauthenticated named pipe, so the marker alone
    // must never be able to make the ELEVATED parent start the command itself - that is
    // exactly what --deelevate exists to prevent. A machine that reports a full split
    // token could have produced a medium child, so the report is a lie there.
    [Fact]
    public void ShouldFailOpen_MarkerReportedWhileThisProcessHasASplitToken_RefusesToLaunch()
        => Assert.False(WSGM.Launch.Program.ShouldFailOpen(
            WSGM.Launch.Program.DisabledUacFailureMessage, hasLinkedLimitedToken: true));

    // UAC off, and equally a built-in Administrator or a standard user: no linked
    // limited token exists, so de-elevation really is impossible and the game must
    // still start (the device case the fail-open was added for).
    [Fact]
    public void ShouldFailOpen_MarkerReportedWithoutALinkedLimitedToken_LaunchesTheGame()
        => Assert.True(WSGM.Launch.Program.ShouldFailOpen(
            WSGM.Launch.Program.DisabledUacFailureMessage, hasLinkedLimitedToken: false));

    // An unqueryable token is not evidence of an attack; keep failing open so a
    // token query that fails can never make every wrapped game unlaunchable.
    [Fact]
    public void ShouldFailOpen_MarkerReportedWithAnUnqueryableToken_LaunchesTheGame()
        => Assert.True(WSGM.Launch.Program.ShouldFailOpen(
            WSGM.Launch.Program.DisabledUacFailureMessage, hasLinkedLimitedToken: null));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void ShouldFailOpen_OrdinaryFailureWithAnyTokenState_RefusesToLaunch(
        bool? hasLinkedLimitedToken)
        => Assert.False(WSGM.Launch.Program.ShouldFailOpen(
            "Process.Start returned no process.", hasLinkedLimitedToken));

    // A peer that embeds the marker in arbitrary text still gets nowhere while the
    // parent's own token says de-elevation was available.
    [Theory]
    [InlineData("Access is denied. UAC appears to be disabled, honest.")]
    [InlineData("UAC appears to be disabled")]
    public void ShouldFailOpen_ForgedMarkerInSurroundingTextWithASplitToken_RefusesToLaunch(
        string error)
    {
        Assert.Contains(WSGM.Launch.Program.NoMediumTokenMarker, error, StringComparison.Ordinal);
        Assert.False(WSGM.Launch.Program.ShouldFailOpen(error, hasLinkedLimitedToken: true));
    }
}
