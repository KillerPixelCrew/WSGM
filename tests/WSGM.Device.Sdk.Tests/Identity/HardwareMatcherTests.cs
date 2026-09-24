using System.Text;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Tests;

namespace WSGM.Device.Sdk.Tests.Identity;

public sealed class HardwareMatcherTests
{
    private static readonly DeviceIdentitySnapshot Claw = new()
    {
        BaseboardManufacturer = "Micro-Star International Co., Ltd.",
        BaseboardProduct = "MS-1T52",
        SystemSku = "1T52.1",
        ProcessorName = "Intel(R) Core(TM) Ultra 7 258V"
    };

    [Fact]
    public void ExactRule_WinsOverAnEarlierFallback()
    {
        HardwareMatchRule fallback = new()
            { BaseboardManufacturer = "Micro-Star International Co., Ltd.", Fallback = true };
        HardwareMatchRule exact = new() { BaseboardProduct = " ms-1t52 ", SystemSku = "1T52.1" };

        var match = HardwareMatcher.Match([fallback, exact], Claw);

        Assert.NotNull(match);
        Assert.False(match.Fallback);
        Assert.Equal(1, match.Index);
        Assert.Equal(2, match.Explanations.Count);
    }

    [Fact]
    public void FallbackMatches_OnlyWhenNoExactRuleDoes()
    {
        HardwareMatchRule fallback = new() { ProcessorNameContains = "Ultra 7", Fallback = true };
        HardwareMatchRule other = new() { BaseboardProduct = "RC72LA" };

        var match = HardwareMatcher.Match([other, fallback], Claw);

        Assert.NotNull(match);
        Assert.True(match.Fallback);
    }

    [Fact]
    public void EmptyRuleAndMismatchedField_NeverMatch()
    {
        Assert.Null(HardwareMatcher.Match([new HardwareMatchRule()], Claw));
        Assert.Null(HardwareMatcher.Match(
            [new HardwareMatchRule { BaseboardProduct = "MS-1T52", SystemSku = "1T52.2" }],
            Claw));
    }

    [Fact]
    public void Manifest_ReadsHardwareCapabilitiesAndWsgmVersion()
    {
        var manifest = PluginManifestFixture.Manifest() with
        {
            Hardware = [new HardwareMatchRule { BaseboardProduct = "MS-1T52" }],
            Capabilities = [CapabilityRole.FanMode, CapabilityRole.ControllerSource],
            WsgmVersion = "2.0.0"
        };

        var result = PluginManifestReader.Read(PluginManifestFixture.Serialize(manifest));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)));
        Assert.Equal("MS-1T52", Assert.Single(result.Manifest!.Hardware).BaseboardProduct);
        Assert.Equal([CapabilityRole.FanMode, CapabilityRole.ControllerSource], result.Manifest.Capabilities);
        Assert.Equal("2.0.0", result.Manifest.WsgmVersion);
    }

    [Fact]
    public void Manifest_RefusesUnknownRolesDuplicateRolesEmptyRulesAndLooseVersions()
    {
        var json = Encoding.UTF8.GetString(PluginManifestFixture.Serialize(PluginManifestFixture.Manifest()));
        var unknownRole = Encoding.UTF8.GetBytes(json[..^1] + ",\"capabilities\":[\"Teleport\"]}");
        Assert.Contains(PluginManifestReader.Read(unknownRole).Errors,
            error => error.Code is ManifestValidationCode.MalformedDocument);

        var manifest = PluginManifestFixture.Manifest() with
        {
            Hardware = [new HardwareMatchRule()],
            Capabilities = [CapabilityRole.FanMode, CapabilityRole.FanMode],
            WsgmVersion = "2.0"
        };
        var errors = PluginManifestReader.Read(PluginManifestFixture.Serialize(manifest)).Errors;

        Assert.Contains(errors, error => error.Path == "hardware[0]");
        Assert.Contains(errors, error => error.Path == "capabilities");
        Assert.DoesNotContain(errors, error => error.Path == "wsgmVersion");
        manifest = manifest with { WsgmVersion = "2.00.1" };
        Assert.Contains(PluginManifestReader.Read(PluginManifestFixture.Serialize(manifest)).Errors,
            error => error.Path == "wsgmVersion");
    }
}
