using System.ComponentModel;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Sdk.Tests.Plugin;

public sealed class DiagnosticTextTests
{
    [Fact]
    public void AFailurePreservesItsNativeCodeAndReadableTextWithoutUnsafeCharacters()
    {
        var result =
            DiagnosticText.FromException("Opening\r\nthe device", new Win32Exception(5, "Denied\u202E\taccess"));

        Assert.Contains("Win32Exception", result);
        Assert.Contains("Denied  access", result);
        Assert.Contains("[error 5]", result);
        Assert.DoesNotContain(result, PlainText.IsUnsafe);
    }

    [Fact]
    public void AnOrdinaryFailureKeepsItsMessageWithoutInventingANativeCode()
    {
        Assert.Equal("Reading (IOException): The device is gone.",
            DiagnosticText.FromException("Reading", new IOException("The device is gone.")));
    }
}
