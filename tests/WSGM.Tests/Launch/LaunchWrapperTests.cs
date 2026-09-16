using System.Collections;
using WSGM.Launch;

namespace WSGM.Tests.Launch;

public sealed class LaunchWrapperTests
{
    // The elevated parent recognizes this exact marker in the child's failure
    // message to fail open when de-elevation is impossible (UAC switched off).
    [Fact]
    public void TheDisabledUacFailureMessageCarriesTheMarkerTheParentMatches()
        => Assert.Contains(
            WSGM.Launch.Program.NoMediumTokenMarker,
            WSGM.Launch.Program.DisabledUacFailureMessage,
            StringComparison.Ordinal);

    [Fact]
    public async Task LaunchPayloadRoundTripsArgumentsEnvironmentAndWorkingDirectory()
    {
        var expected = new LaunchPayload(
            @"C:\Games\Emulator",
            [@"C:\Games\Emulator\Ryujinx.exe", "--fullscreen", "value with spaces", "雪"],
            [KeyValuePair.Create("SteamAppId", "1234"), KeyValuePair.Create("EMPTY", "")]);
        await using var stream = new MemoryStream();

        await expected.WriteAsync(stream, CancellationToken.None);
        stream.Position = 0;
        var actual = await LaunchPayload.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
        Assert.Equal(expected.Arguments, actual.Arguments);
        Assert.Equal(expected.EnvironmentVariables, actual.EnvironmentVariables);
    }

    [Theory]
    [InlineData("SDL_GAMECONTROLLER_IGNORE_DEVICES")]
    [InlineData("sdl_gamecontroller_ignore_devices")]
    public async Task LaunchPayloadAlwaysRemovesTheSdlExclusionFromTheChildOnly(string exclusionName)
    {
        var previous = Environment.GetEnvironmentVariable(exclusionName);
        var previousAppId = Environment.GetEnvironmentVariable("SteamAppId");
        try
        {
            Environment.SetEnvironmentVariable(exclusionName, "0x28de/0x1205");
            Environment.SetEnvironmentVariable("SteamAppId", "1234");
            var payload = LaunchPayload.Capture(["game.exe", "雪"]);
            await using var stream = new MemoryStream();
            await payload.WriteAsync(stream, CancellationToken.None);
            stream.Position = 0;
            var received = await LaunchPayload.ReadAsync(stream, CancellationToken.None);

            Assert.DoesNotContain(received.EnvironmentVariables, pair =>
                pair.Key.Equals(exclusionName, StringComparison.OrdinalIgnoreCase));
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry is { Key: string key, Value: string value }
                    && !key.Equals(exclusionName, StringComparison.OrdinalIgnoreCase))
                {
                    Assert.True(received.EnvironmentVariables.Any(pair => pair.Key == key && pair.Value == value),
                        "An unrelated child environment entry changed.");
                }
            }
            Assert.Contains(KeyValuePair.Create("SteamAppId", "1234"), received.EnvironmentVariables);
            Assert.Equal(["game.exe", "雪"], received.Arguments);
            Assert.Equal("0x28de/0x1205", Environment.GetEnvironmentVariable(exclusionName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(exclusionName, previous);
            Environment.SetEnvironmentVariable("SteamAppId", previousAppId);
        }
    }

    [Fact]
    public void ScheduledTaskUsesInteractiveTokenWithoutAnElevatedRunLevel()
    {
        var xml = ScheduledTaskLauncher.BuildTaskXml(
            @"C:\A&B\WSGM.Launch.exe", "pipe<name>");

        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.DoesNotContain("<RunLevel>", xml);
        Assert.Contains(@"<Command>C:\A&amp;B\WSGM.Launch.exe</Command>", xml);
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
            ["--deelevate", "--", @"C:\Program Files\game.exe", "-map", "de dust"],
            out var options,
            out _));
        Assert.Equal([@"C:\Program Files\game.exe", "-map", "de dust"], options.Command);
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
