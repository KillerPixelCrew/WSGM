using WSGM.Device.Sdk.Capabilities;
using WSGM.Testing;

namespace WSGM.Plugin.Sdk.Tests.Boundaries;

public sealed class ContractBoundaryTests
{
    [Fact]
    public void CommonPluginContractsUseOneSdkAssemblyWithoutHardwareRuntimeScaffolding()
    {
        var contract = RepositoryFiles.LoadProject("src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj");
        var assembly = typeof(PluginApi).Assembly;

        Assert.Equal(assembly, typeof(CapabilityValue).Assembly);
        Assert.DoesNotContain(contract.Descendants("ProjectReference"), reference =>
            ((string?)reference.Attribute("Include"))?.Contains("WSGM.Device.Sdk") == true);
        Assert.Empty(contract.Descendants("PackageReference"));
        Assert.Null(assembly.GetType("WSGM.Device.Sdk.Plugin.IDevicePlugin"));
        Assert.Null(assembly.GetType("WSGM.Device.Sdk.Services.DeviceServiceLifecycle"));
        Assert.Null(assembly.GetType("WSGM.Device.Sdk.Windows.LegacyMotionSensors"));
    }

    [Fact]
    public void TheContractDocumentsEveryPublicMemberOrFailsTheBuild()
    {
        var contract = RepositoryFiles.LoadProject("src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj");
        Assert.Contains(contract.Descendants("GenerateDocumentationFile"), element => element.Value == "true");
        var warnings = string.Join(';', contract.Descendants("WarningsAsErrors").Select(element => element.Value));
        Assert.Contains("CS1591", warnings);
        Assert.Contains("CS1573", warnings);
    }

    [Fact]
    public void TheCommonApiVersionIsAnExplicitAdmissionFloor()
    {
        Assert.Equal(5, PluginApi.Version);
    }
}
