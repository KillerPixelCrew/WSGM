using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     Which activation selects the Xbox app's own dashboard, and when the stored choice has to be set.
///     The UWP app it otherwise hands off to cannot show its view in Game Mode.
/// </summary>
public sealed class XboxAppExperienceTests
{
    [Theory]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App", true)]
    [InlineData("microsoft.gamingapp_8wekyb3d8bbwe!microsoft.xbox.app", true)]
    [InlineData(" Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App ", true)]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.AppL", false)]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.WidgetsApp", false)]
    [InlineData("Microsoft.XboxGamingOverlay_8wekyb3d8bbwe!App", false)]
    [InlineData("Publisher.Game_abc123!App", false)]
    public void OnlyTheXboxAppEntryIsSteered(string aumid, bool expected)
    {
        Assert.Equal(expected, XboxAppExperience.Applies(aumid));
    }

    [Fact]
    public void OnlyAStoredDwordOfOneSkipsTheWrite()
    {
        Assert.False(XboxAppExperience.NeedsSelection(1));
        Assert.True(XboxAppExperience.NeedsSelection(null));
        Assert.True(XboxAppExperience.NeedsSelection(0));
        Assert.True(XboxAppExperience.NeedsSelection(2));
        Assert.True(XboxAppExperience.NeedsSelection("1"));
        Assert.True(XboxAppExperience.NeedsSelection(1L));
    }
}
