using System.Text;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Packaging;

namespace WSGM.Device.Tests;

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

    private static string Describe(PluginManifestReadResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Path}: {error.Message}"));
}
