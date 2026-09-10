using System.Globalization;
using WSGM.Core;

namespace WSGM.Tests;

/// <summary>
/// The standby diagnostic's own rules. It reads the live machine, so what is asserted here is the
/// shape of what it may say — never a value that depends on how this machine happens to be sleeping.
/// </summary>
public sealed class ModernStandbyDiagnosticsTests
{
    [Theory]
    [InlineData(0, 0, "seconds")]
    [InlineData(45, 45, "seconds")]
    [InlineData(90, 2, "minutes")]
    [InlineData(3599, 60, "minutes")]
    [InlineData(3600, 1.0, "hours")]
    [InlineData(81801, 22.7, "hours")]
    public void ADurationIsDescribedInTheUnitSomeoneWouldSayItIn(int seconds, double value, string unit)
    {
        // The number is formatted for the user's culture, so the expectation is built the same way
        // rather than assuming a decimal point — this box runs in German, where it is a comma.
        // The last case is the reference handheld's measured 22h43m standby.
        string expected = unit == "hours"
            ? string.Create(CultureInfo.CurrentCulture, $"{value:0.0} {unit}")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0} {unit}");

        Assert.Equal(expected, ModernStandbyDiagnostics.Describe(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ReadingNeverThrowsAndAlwaysSaysSomething()
    {
        // A diagnostic that could throw would take the settings page with it, and one that could
        // return nothing would leave a blank row that reads as a bug.
        ModernStandbyReport report = ModernStandbyDiagnostics.Read();

        Assert.False(string.IsNullOrWhiteSpace(report.Summary));
        Assert.NotNull(report.ArmedWakeSources);
    }

    [Fact]
    public void AnUnsupportedMachineIsToldSoRatherThanOfferedTheFeature()
    {
        // "Degrade safely on machines that do not support Modern Standby" is only honest if the
        // machine is told; a supported one must never carry the unsupported wording.
        ModernStandbyReport report = ModernStandbyDiagnostics.Read();

        if (report.Supported)
        {
            Assert.DoesNotContain("does not report Modern Standby", report.Summary, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(report.ArmedWakeSources);
        }
    }

    [Fact]
    public void TheSummaryNeverNamesACauseForTheWake()
    {
        // Windows exposes no documented call for what woke the machine. Naming one would be a guess
        // presented as a diagnosis, which is the specific thing this text refuses to do.
        ModernStandbyReport report = ModernStandbyDiagnostics.Read();

        Assert.DoesNotContain("woken by", report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("caused by", report.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
