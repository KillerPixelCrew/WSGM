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
}
