using WSGM.DeviceLab.Core.Capture;

namespace WSGM.DeviceLab.Tests;

/// <summary>
/// The executable specification of capture redaction: what a shared capture must lose, and what it
/// must keep to remain useful.
/// </summary>
public class RedactionTests
{
    [Fact]
    public void ADeviceInstancePath_KeepsTheModelAndLosesTheInstance()
    {
        // The VID/PID come from the descriptor and are identical on every unit of a model, so they
        // describe the hardware. The serial after them describes this machine.
        string redacted = new CaptureRedactor()
            .Redact(@"USB\VID_0DB0&PID_1901\00006F64096B22E7");

        Assert.Contains("VID_0DB0&PID_1901", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("00006F64096B22E7", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherControllerModesEnumerationPath_IsAlsoRedacted()
    {
        // This device exposes a serial in one mode and a hub/port path in the other. Both identify
        // the machine, so a rule that only knew about serials would leak the second form.
        string redacted = new CaptureRedactor()
            .Redact(@"USB\VID_0DB0&PID_1902\5&17FBE650&0&2");

        Assert.Contains("VID_0DB0&PID_1902", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("17FBE650", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"HID\VID_0DB0&PID_1901&IG_00\8&1717EFAA&0&0000", "1717EFAA")]
    [InlineData(@"HID\VID_0DB0&PID_1901&MI_02&COL01\7&338A39EB&0&0000", "338A39EB")]
    [InlineData(@"USB\VID_0DB0&PID_1901&MI_00\6&2B02AE9F&0&0000", "2B02AE9F")]
    public void EveryDescriptorSuffixFormIsRedacted(string path, string instanceFragment)
    {
        // Found by running the redactor against this machine's real inventory rather than fixtures:
        // an earlier pattern allowed only &MI_xx and left the XInput &IG_00 form completely
        // unredacted. Every suffix shape the hardware actually produces is covered here.
        string redacted = new CaptureRedactor().Redact(path);

        Assert.DoesNotContain(instanceFragment, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VID_0DB0&PID_1901", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameDeviceKeepsTheSameTokenAcrossEvents()
    {
        // A developer reading a shared capture still has to be able to follow one device through a
        // sequence of events. Removing the value outright would break that.
        CaptureRedactor redactor = new();
        const string Path = @"USB\VID_0DB0&PID_1901\00006F64096B22E7";

        Assert.Equal(redactor.Redact(Path), redactor.Redact(Path));
    }

    [Fact]
    public void DifferentDevicesGetDifferentTokens()
    {
        CaptureRedactor redactor = new();

        string first = redactor.Redact(@"USB\VID_0DB0&PID_1901\AAAA1111");
        string second = redactor.Redact(@"USB\VID_0DB0&PID_1901\BBBB2222");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ASecurityIdentifierIsRemoved()
    {
        string redacted = new CaptureRedactor().Redact("owner=S-1-5-21-1004336348-1177238915-682003330-512");

        Assert.DoesNotContain("S-1-5-21", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfilePathIsRemoved()
    {
        string redacted = new CaptureRedactor()
            .Redact(@"config at C:\Users\SomeOperator\AppData\Local\WSGM");

        Assert.DoesNotContain("SomeOperator", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AMacAddressIsRemoved()
    {
        string redacted = new CaptureRedactor().Redact("adapter 3C-7C-3F-1A-2B-9D active");

        Assert.DoesNotContain("3C-7C-3F", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCurrentUserAndMachineNamesAreRemoved()
    {
        // The names of whoever is running the sweep, which appear in log lines and paths that no
        // pattern would otherwise recognise.
        string redacted = new CaptureRedactor()
            .Redact($"{Environment.UserName} on {Environment.MachineName}");

        if (Environment.UserName.Length > 2)
        {
            Assert.DoesNotContain(Environment.UserName, redacted, StringComparison.OrdinalIgnoreCase);
        }

        if (Environment.MachineName.Length > 2)
        {
            Assert.DoesNotContain(Environment.MachineName, redacted, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ASidInsideAResolvedAccountString_IsRemovedWithTheName()
    {
        // Order matters: replacing the account name first would leave a half-redacted composite.
        CaptureRedactor redactor = new();

        string redacted = redactor.Redact(
            $@"S-1-5-21-1-2-3-1001 ({Environment.MachineName}\{Environment.UserName})");

        Assert.DoesNotContain("S-1-5-21-1-2-3-1001", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactionIsSummarizedSoTheReaderKnowsSomethingWasRemoved()
    {
        // Absent and redacted must be distinguishable, or a developer cannot tell a missing value
        // from a scrubbed one.
        CaptureRedactor redactor = new();
        redactor.Redact(@"USB\VID_0DB0&PID_1901\AAAA1111");
        redactor.Redact("S-1-5-21-1-2-3-1001");

        IReadOnlyList<RedactionSummary> summary = redactor.Summarize();

        Assert.Contains(summary, s => s.Category == RedactionCategory.DeviceInstance);
        Assert.Contains(summary, s => s.Category == RedactionCategory.SecurityIdentifier);
    }

    [Fact]
    public void TextWithNothingIdentifyingPassesThroughUnchanged()
    {
        const string Clean = "MSI_ACPI Get_Data returned 32 bytes, status 0x01";

        Assert.Equal(Clean, new CaptureRedactor().Redact(Clean));
    }

    [Fact]
    public void NullAndEmptyAreHandled()
    {
        CaptureRedactor redactor = new();

        Assert.Equal(string.Empty, redactor.Redact(null));
        Assert.Equal(string.Empty, redactor.Redact(string.Empty));
    }
}
