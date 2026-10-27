using System.Text.Json;
using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class HidHideInstallerTests
{
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(-536870911, false, false)]
    public void VerifiedInstallerUsesItsVendorSwitchesAndRecordsOnlySuccessfulInstallation(
        int exitCode, bool succeeded, bool restart)
    {
        using SetupTestInstallation installation = new();
        using var stream = typeof(SetupEngine).Assembly.GetManifestResourceStream("WSGM.ControllerComponentsPin");
        Assert.NotNull(stream);
        using var pin = JsonDocument.Parse(stream);
        var component = pin.RootElement.GetProperty("components").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "hidhide");
        var calls = 0;
        installation.Runtime.OnRun = (executable, arguments) =>
        {
            Assert.Equal("verified-hidhide.exe", executable);
            Assert.Equal("/exenoui /qn /norestart", arguments);
            calls++;
            return exitCode;
        };
        var step = new SetupStep("Installing HidHide", "HidHide installed", false, _ => true);

        Assert.Equal(succeeded,
            installation.Engine.RunVerifiedHidHideInstaller(step, "verified-hidhide.exe", component));

        Assert.Equal(1, calls);
        Assert.Equal(succeeded, installation.Engine.Components.HidHide);
        Assert.Equal(restart, installation.Engine.RestartRequired);
        var record = Path.Combine(installation.Machine, "components.json");
        if (succeeded)
        {
            Assert.True(InstalledComponents.Read(record).HidHide);
        }
        else
        {
            Assert.False(File.Exists(record));
            Assert.Contains(exitCode.ToString(), step.Note, StringComparison.Ordinal);
        }
    }
}
