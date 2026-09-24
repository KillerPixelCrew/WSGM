using WSGM.Setup.Engine;

namespace WSGM.Tests.Setup;

public sealed class UsbipOutcomeTests
{
    private static string Ini(string outcome, string reboot = "false", string message = "", string schema = "1")
    {
        return $"[usbip]\r\nschemaVersion={schema}\r\noutcome={outcome}\r\nrequiredVersion=0.9.8.0\r\n"
               + $"observedVersion=0.9.8.0\r\ndriverRegistered=true\r\nrebootRequired={reboot}\r\nmessage={message}\r\n";
    }

    [Theory]
    [InlineData("installed")]
    [InlineData("already-present")]
    public void PresentDriver_Succeeds(string outcome)
    {
        var parsed = UsbipOutcome.Parse(Ini(outcome, "true"));

        Assert.True(parsed.Succeeded);
        Assert.True(parsed.RebootRequired);
        Assert.Equal(outcome, parsed.Outcome);
    }

    [Fact]
    public void FailedDriver_CarriesTheScriptsMessage()
    {
        var parsed = UsbipOutcome.Parse(Ini("blocked-newer-version", message: "A newer usbip-win2 is installed."));

        Assert.False(parsed.Succeeded);
        Assert.StartsWith("A newer usbip-win2 is installed.", parsed.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2", "installed")]
    [InlineData("1", "report-only")]
    [InlineData("1", "")]
    public void UnknownSchemaOrOutcome_IsAFailureNotASuccess(string schema, string outcome)
    {
        Assert.False(UsbipOutcome.Parse(Ini(outcome, schema: schema)).Succeeded);
    }
}
