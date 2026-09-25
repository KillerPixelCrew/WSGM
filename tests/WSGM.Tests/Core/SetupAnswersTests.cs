using System.Text;
using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class SetupAnswersTests
{
    [Fact]
    public void ExportThenApply_RoundTripsEverySetupChoice()
    {
        var config = new AppConfig { StartAtSignIn = false, StartMode = SessionStartMode.Desktop };
        config.Cef.ConnectedLibraryCarousel = false;
        config.GamepadChord.Enabled = true;
        config.Performance.Enabled = true;

        var exported = SetupAnswers.Export(config, false, ["Steam (HKCU Run)"]);
        var read = SetupAnswers.Parse(exported.ToUtf8Json());
        AppConfig applied = new();
        read.ApplyTo(applied, freshInstall: false);

        Assert.False(applied.StartAtSignIn);
        Assert.Equal(SessionStartMode.Desktop, applied.StartMode);
        Assert.False(applied.Cef.ConnectedLibraryCarousel);
        Assert.True(applied.GamepadChord.Enabled);
        Assert.True(applied.Performance.Enabled);
        Assert.Equal(["Steam (HKCU Run)"], read.SteamAutostartEntries);
        Assert.Equal(["full", "minimal"], read.Presets.Keys.Order());
    }

    [Fact]
    public void MinimalPreset_LeavesSteamAloneButKeepsWsgmReachable()
    {
        AppConfig config = new();
        new SetupAnswers { Features = SetupFeatures.Minimal, StartMode = SessionStartMode.Game }.ApplyTo(config, freshInstall: true);

        Assert.False(config.SteamInputManagementEnabled);
        Assert.False(config.Cef.Enabled);
        Assert.False(config.SteamAutostartTakeoverAccepted);
        Assert.True(config.Gestures.TopEdge);
        Assert.True(config.Hotkey.Enabled);
        Assert.False(config.Performance.Enabled);
    }

    [Fact]
    public void FullPreset_TurnsOnRtssPerformanceControls()
    {
        AppConfig config = new();
        new SetupAnswers { Features = SetupFeatures.Full }.ApplyTo(config);

        Assert.True(config.Performance.Enabled);
    }

    [Fact]
    public void OtherManagersConsent_SurvivesARunThatFoundNothingToTurnOff()
    {
        AppConfig config = new();
        new SetupAnswers { Features = SetupFeatures.Full, OtherManagersTakeover = true }.ApplyTo(config);

        Assert.Empty(config.OtherManagersDisabled);
        Assert.True(SetupAnswers.Export(config, false, []).OtherManagersTakeover);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void FreshSetup_EnablesControllerManagementWithDeviceIntegration(bool integration, bool expected)
    {
        AppConfig config = new();
        var answers = new SetupAnswers { Features = SetupFeatures.Full, DeviceIntegration = integration };

        answers.ApplyTo(config, freshInstall: true);

        Assert.Equal(expected, config.DeviceIntegration.ControllerManagementEnabled);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ExistingSetup_PreservesControllerChoice(bool integration, bool management)
    {
        AppConfig config = new();
        config.DeviceIntegration.ControllerManagementEnabled = management;
        // The actual installation state wins over the exported answer's stale flag.
        var answers = new SetupAnswers { Features = SetupFeatures.Full, DeviceIntegration = integration, FreshInstall = true };

        answers.ApplyTo(config, freshInstall: false);

        Assert.Equal(management, config.DeviceIntegration.ControllerManagementEnabled);
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"features":{}}""")]
    [InlineData("""{"schemaVersion":1,"startMode":"Nowhere","features":{}}""")]
    [InlineData("not json")]
    public void MalformedAnswers_AreRefused(string json)
    {
        Assert.Throws<InvalidDataException>(() => SetupAnswers.Parse(Encoding.UTF8.GetBytes(json)));
    }
}
