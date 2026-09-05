using System.Text.Json;
using WSGM.Device.Sdk;
using Xunit;

namespace WSGM.Device.HandheldCompanion.Tests;

public sealed class ScaffoldManifestTests
{
    [Fact]
    public void ManifestTargetsTheSharedSdkApiLevel()
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "plugin.wsgm.json")));

        Assert.Equal(DeviceApi.Version, manifest.RootElement.GetProperty("apiVersion").GetInt32());
    }
}
