using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;

namespace WSGM.Tests;

/// <summary>Regression tests for value objects and pure presentation branches that
/// must remain runnable without touching the user's shell, hardware, or registry.</summary>
public sealed class RegressionCoverageTests
{
    [Theory]
    [InlineData((GamepadButtons)0, false, "None")]
    [InlineData(GamepadButtons.RightTrigger | GamepadButtons.L4 | GamepadButtons.QuickAccess, false, "R2 + L4 + Quick Access")]
    [InlineData(GamepadButtons.DPadUp | GamepadButtons.RightPadPress, true, "Hold D-Up + R-Pad")]
    public void GamepadDescriptionsCoverEmptyAndExtendedButtons(GamepadButtons buttons, bool hold, string expected)
        => Assert.Equal(expected, GamepadService.Describe(buttons, hold));

    [Theory]
    [InlineData("C:\\Tools\\", "C:\\Tools\\")]
    [InlineData("C:\\Program Files\\", "\"C:\\Program Files\\\\\"")]
    [InlineData("say \"hello\"", "\"say \\\"hello\\\"\"")]
    public void QuotePreservesTrailingBackslashesAndEmbeddedQuotes(string argument, string expected)
        => Assert.Equal(expected, SelfElevation.Quote(argument));

    [Fact]
    public void PadSnapshotKeepsItsControllerIdentityAndButtons()
    {
        var snapshot = new SdlGamepads.PadSnapshot(42, GamepadButtons.A | GamepadButtons.Start);

        Assert.Equal(42u, snapshot.Id);
        Assert.Equal(GamepadButtons.A | GamepadButtons.Start, snapshot.Buttons);
    }

    [Fact]
    public void LaunchResultIsAnImmutableValueSummary()
    {
        var result = new AppLauncher.LaunchResult(null, Started: false, ElevationDeclined: true);

        Assert.False(result.Started);
        Assert.True(result.ElevationDeclined);
        Assert.Null(result.Process);
    }

    [Fact]
    public void WindowEntryPreservesTheActivationTargetAndPresentationState()
    {
        var entry = new AppWindowEntry((nint)123, "Steam", isSteam: true);

        Assert.Equal((nint)123, entry.Hwnd);
        Assert.Equal("Steam", entry.Title);
        Assert.True(entry.IsSteam);
    }

    [Fact]
    public void RegistryAndWindowSnapshotsRetainTheirPositionalRecordContracts()
    {
        var uac = new UacSettings.UacState(true, 0, 1, 1);
        var window = new WindowFinder.AppWindow((nint)456, "Game", 789);

        var (readable, consentPrompt, secureDesktop, enableLua) = uac;
        var (hwnd, title, processId) = window;

        Assert.True(readable);
        Assert.Equal(0, consentPrompt);
        Assert.Equal(1, secureDesktop);
        Assert.Equal(1, enableLua);
        Assert.Equal((nint)456, hwnd);
        Assert.Equal("Game", title);
        Assert.Equal(789u, processId);
    }

    [Fact]
    public void NormalizeKeepsExistingNestedSectionsAndCollections()
    {
        var apps = new List<StartupAppConfig>();
        var hotkey = new HotkeyConfig { Enabled = true, VirtualKey = 0x41 };
        var chord = new GamepadChordConfig { Enabled = true, Buttons = (int)GamepadButtons.A };
        var gestures = new GestureConfig { BottomEdge = true };
        var config = new AppConfig
        {
            StartupApps = apps,
            Hotkey = hotkey,
            GamepadChord = chord,
            Gestures = gestures,
        };

        var normalized = ConfigStore.Normalize(config);

        Assert.Same(config, normalized);
        Assert.Same(apps, normalized.StartupApps);
        Assert.Same(hotkey, normalized.Hotkey);
        Assert.Same(chord, normalized.GamepadChord);
        Assert.Same(gestures, normalized.Gestures);
    }

    [Theory]
    [InlineData("WSGM.exe", "WSGM.exe")]
    [InlineData("C:\\Tools\\app.exe\t--argument", "C:\\Tools\\app.exe\t--argument")]
    [InlineData("\"C:\\Tools\\app.exe\"", "C:\\Tools\\app.exe")]
    public void ShellCommandParserUsesWinlogonSpaceSemantics(string command, string expected)
        => Assert.Equal(expected, ShellRegistration.ExtractExecutablePath(command));
}
