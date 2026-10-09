using WSGM.Device.Sdk.Input;
using WSGM.Plugin.Sdk;
using WSGM.Testing;

namespace WSGM.Device.Sdk.Tests.Boundaries;

public sealed class ContractBoundaryTests
{
    [Fact]
    public void SharedDeviceContractsLiveInTheSinglePluginSdkAssembly()
    {
        var contract = RepositoryFiles.LoadProject("src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj");

        Assert.Equal(typeof(PluginApi).Assembly, typeof(CanonicalControllerSample).Assembly);
        Assert.DoesNotContain(contract.Descendants("ProjectReference"), reference =>
            ((string?)reference.Attribute("Include"))?.Contains("WSGM.Device.Sdk") == true);
        Assert.Empty(contract.Descendants("PackageReference"));
    }

    [Fact]
    public void TheContractDocumentsEveryPublicMemberOrFailsTheBuild()
    {
        // Guarding the setting rather than the members: a plugin author reads this contract
        // through IntelliSense, so the enforcement disappearing is the regression worth catching.
        var contract = RepositoryFiles.LoadProject("src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj");

        Assert.Contains(contract.Descendants("GenerateDocumentationFile"), element => element.Value == "true");
        var warnings = string.Join(';', contract.Descendants("WarningsAsErrors").Select(element => element.Value));
        Assert.Contains("CS1591", warnings);
        Assert.Contains("CS1573", warnings);
    }

    [Fact]
    public void TheApiVersionIsPinnedSoRaisingItIsADeliberateAct()
    {
        // Common plugins require API 5 exactly because the retained device contract types now
        // have the Plugin SDK's assembly identity. DeviceApi remains archive metadata only.
        Assert.Equal(5, PluginApi.Version);
        Assert.Equal(12, DeviceApi.Version);
    }

    [Fact]
    public void HighRateInputSamplesAreValueTypes()
    {
        Assert.True(typeof(CanonicalControllerSample).IsValueType);
        Assert.True(typeof(MotionSample).IsValueType);
    }
}
