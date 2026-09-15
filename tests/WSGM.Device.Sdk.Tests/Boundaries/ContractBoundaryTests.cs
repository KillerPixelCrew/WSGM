using System.Xml.Linq;

namespace WSGM.Device.Tests;

public sealed class ContractBoundaryTests
{
    [Fact]
    public void TheContractDocumentsEveryPublicMemberOrFailsTheBuild()
    {
        // Guarding the setting rather than the members: a plugin author reads this contract
        // through IntelliSense, so the enforcement disappearing is the regression worth catching.
        XDocument contract = RepositoryFiles.LoadProject("src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj");

        Assert.Contains(contract.Descendants("GenerateDocumentationFile"), element => element.Value == "true");
        string warnings = string.Join(';', contract.Descendants("WarningsAsErrors").Select(element => element.Value));
        Assert.Contains("CS1591", warnings);
        Assert.Contains("CS1573", warnings);
    }

    [Fact]
    public void TheApiVersionIsPinnedSoRaisingItIsADeliberateAct()
    {
        // PluginManifestValidator requires exact equality, so every raise invalidates every
        // published package. Version 2 added sections and categories; version 3 added the
        // suppressed trace level and TraceChange.
        Assert.Equal(3, WSGM.Device.Sdk.DeviceApi.Version);
    }
}
