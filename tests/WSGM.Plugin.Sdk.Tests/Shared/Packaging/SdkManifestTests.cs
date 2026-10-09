using System.Text;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Packaging;
using WSGM.Testing;

namespace WSGM.Device.Sdk.Tests.Packaging;

public sealed class SdkManifestTests
{
    [Fact]
    public void Read_ExactSixFieldManifest_UsesTheOneRuntimeApi()
    {
        var result = PluginManifestReader.Read(PluginManifestFixture.Serialize(PluginManifestFixture.Manifest()));

        Assert.True(result.IsValid, Describe(result));
        Assert.Equal(DeviceApi.Version, result.Manifest!.ApiVersion);
        Assert.Equal("Synthetic.Dock.Plugin", result.Manifest.EntryType);
    }

    [Fact]
    public void Read_UnknownRetiredField_IsRejectedInsteadOfBecomingCompatibilitySurface()
    {
        var json = Encoding.UTF8.GetString(PluginManifestFixture.Serialize(PluginManifestFixture.Manifest()));
        var withRetiredField = Encoding.UTF8.GetBytes(
            json[..^1] + ",\"schemaVersion\":1}");

        var result = PluginManifestReader.Read(withRetiredField);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code is ManifestValidationCode.MalformedDocument);
    }

    [Fact]
    public void Read_DifferentApiAndTraversalAssembly_ReportBothFailures()
    {
        var manifest = PluginManifestFixture.Manifest() with
        {
            ApiVersion = DeviceApi.Version + 1,
            EntryAssembly = "../Synthetic.Dock.dll"
        };

        var result = PluginManifestReader.Read(PluginManifestFixture.Serialize(manifest));

        Assert.Contains(result.Errors, error => error.Code is ManifestValidationCode.InvalidApiVersion);
        Assert.Contains(result.Errors, error => error.Code is ManifestValidationCode.UnsafePath);
    }

    [Fact]
    public void Read_FieldsAreCheckedForShapeNotLengthOrCount()
    {
        // The document's byte bound already bounds every field, so a long identity or many hardware
        // rules is a valid package, not a hostile one.
        var manifest = PluginManifestFixture.Manifest() with
        {
            Id = "wsgm.device." + new string('a', 300),
            Name = new string('N', 300),
            Hardware =
            [
                .. Enumerable.Range(0, 40).Select(index => new HardwareMatchRule
                {
                    SystemModel = $"Model {index} " + new string('m', 200)
                })
            ]
        };

        var result = PluginManifestReader.Read(PluginManifestFixture.Serialize(manifest));

        Assert.True(result.IsValid, Describe(result));
    }

    [Fact]
    public void Read_HardwareFieldWithAControlCharacter_IsInvalidText()
    {
        var manifest = PluginManifestFixture.Manifest() with
        {
            Hardware = [new HardwareMatchRule { SystemModel = "Claw\nA1M" }]
        };

        var result = PluginManifestReader.Read(PluginManifestFixture.Serialize(manifest));

        Assert.Contains(result.Errors, error => error.Code is ManifestValidationCode.InvalidText);
    }

    private static string Describe(PluginManifestReadResult result)
    {
        return string.Join("; ", result.Errors.Select(error => $"{error.Path}: {error.Message}"));
    }
}
