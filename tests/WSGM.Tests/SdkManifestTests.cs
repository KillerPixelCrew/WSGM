using System.Text;
using System.Text.Json;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Sdk.Serialization;

namespace WSGM.Device.Tests;

public sealed class SdkManifestTests
{
    [Fact]
    public void Read_ExactSixFieldManifest_UsesTheOneRuntimeApi()
    {
        PluginManifestReadResult result = PluginManifestReader.Read(Serialize(Manifest()));

        Assert.True(result.IsValid, Describe(result));
        Assert.Equal(DeviceApi.Version, result.Manifest!.ApiVersion);
        Assert.Equal("Synthetic.Dock.Plugin", result.Manifest.EntryType);
    }

    [Fact]
    public void Read_UnknownRetiredField_IsRejectedInsteadOfBecomingCompatibilitySurface()
    {
        string json = Encoding.UTF8.GetString(Serialize(Manifest()));
        byte[] withRetiredField = Encoding.UTF8.GetBytes(
            json[..^1] + ",\"schemaVersion\":1}");

        PluginManifestReadResult result = PluginManifestReader.Read(withRetiredField);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code is ManifestValidationCode.MalformedDocument);
    }

    [Fact]
    public void Read_DifferentApiAndTraversalAssembly_ReportBothFailures()
    {
        PluginManifest manifest = Manifest() with
        {
            ApiVersion = DeviceApi.Version + 1,
            EntryAssembly = "../Synthetic.Dock.dll",
        };

        PluginManifestReadResult result = PluginManifestReader.Read(Serialize(manifest));

        Assert.Contains(result.Errors, error => error.Code is ManifestValidationCode.InvalidApiVersion);
        Assert.Contains(result.Errors, error => error.Code is ManifestValidationCode.UnsafePath);
    }

    internal static PluginManifest Manifest() => new()
    {
        Id = "wsgm.device.synthetic.dock-x1",
        Name = "Synthetic Dock X1",
        Version = "1.0.0",
        ApiVersion = DeviceApi.Version,
        EntryAssembly = "Synthetic.Dock.dll",
        EntryType = "Synthetic.Dock.Plugin",
    };

    internal static byte[] Serialize(PluginManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, DeviceJsonContext.Default.PluginManifest);

    private static string Describe(PluginManifestReadResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Path}: {error.Message}"));
}
