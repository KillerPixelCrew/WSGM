using System.Xml.Linq;

namespace WSGM.Device.Tests;

public sealed class ContractBoundaryTests
{
    [Fact]
    public void TheContractHasNoProjectOrPackageDependencies()
    {
        // This assembly is the type-identity boundary between the host and a plugin, and it is
        // linked into the plugin. Anything referenced here is handed to every plugin ever written
        // against this contract, and cannot be taken back without breaking all of them.
        XDocument contract = LoadProject("src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj");

        Assert.Empty(contract.Descendants("ProjectReference"));
        Assert.Empty(contract.Descendants("PackageReference"));
    }

    [Fact]
    public void TheContractDocumentsEveryPublicMemberOrFailsTheBuild()
    {
        // Guarding the setting rather than the members: a plugin author reads this contract
        // through IntelliSense, so the enforcement disappearing is the regression worth catching.
        XDocument contract = LoadProject("src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj");

        Assert.Equal(
            "true",
            contract.Descendants("GenerateDocumentationFile").Single().Value);
        Assert.Contains(
            "CS1591",
            contract.Descendants("WarningsAsErrors").Single().Value);
        Assert.Contains(
            "CS1573",
            contract.Descendants("WarningsAsErrors").Single().Value);
    }

    private static XDocument LoadProject(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativePath));

    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null
                && !File.Exists(Path.Combine(directory.FullName, "WSGM.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new InvalidOperationException("The repository root was not found.");
        }
    }
}
