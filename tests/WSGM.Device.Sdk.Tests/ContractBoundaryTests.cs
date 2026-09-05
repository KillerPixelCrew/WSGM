using System.Xml.Linq;

namespace WSGM.Device.Tests;

public sealed class ContractBoundaryTests
{
    [Fact]
    public void TheContractDocumentsEveryPublicMemberOrFailsTheBuild()
    {
        // Guarding the setting rather than the members: a plugin author reads this contract
        // through IntelliSense, so the enforcement disappearing is the regression worth catching.
        XDocument contract = LoadProject("src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj");

        Assert.Contains(contract.Descendants("GenerateDocumentationFile"), element => element.Value == "true");
        string warnings = string.Join(';', contract.Descendants("WarningsAsErrors").Select(element => element.Value));
        Assert.Contains("CS1591", warnings);
        Assert.Contains("CS1573", warnings);
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
