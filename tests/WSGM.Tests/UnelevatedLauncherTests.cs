using WSGM.Core;

namespace WSGM.Tests;

/// <summary>Task XML generation for the de-elevating scheduled-task launcher.</summary>
public sealed class UnelevatedLauncherTests
{
    [Fact]
    public void DeElevationTaskEscapesExecutableAndArgumentsInXml()
    {
        var xml = UnelevatedLauncher.BuildTaskXml("C:\\A&B\\WSGM.exe", "--open-<wifi>-settings");

        Assert.Contains("<Command>C:\\A&amp;B\\WSGM.exe</Command>", xml);
        Assert.Contains("<Arguments>--open-&lt;wifi&gt;-settings</Arguments>", xml);
    }

    [Fact]
    public void DeElevationTaskUsesInteractiveTokenWithoutAnElevatedRunLevelInUtf16()
    {
        // The three properties invariant 5 rests on: an InteractiveToken principal with
        // NO RunLevel element yields the user's filtered medium-IL token (a RunLevel of
        // HighestAvailable would hand Explorer and the ms-settings one-shot back their
        // elevation), and schtasks rejects anything but the UTF-16 declaration with
        // "cannot switch encoding".
        var xml = UnelevatedLauncher.BuildTaskXml("C:\\WSGM\\WSGM.exe");

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.DoesNotContain("<RunLevel>", xml);
    }
}
