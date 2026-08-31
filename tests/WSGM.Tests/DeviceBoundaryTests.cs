using System.Xml.Linq;

namespace WSGM.Tests;

public sealed class DeviceBoundaryTests
{
    [Fact]
    public void WsgmLoadsThePluginDynamicallyWithoutReferencingItsProject()
    {
        string[] references = ProjectReferences("src/WSGM/WSGM.csproj").ToArray();

        // The SDK is the only device reference WSGM may hold: it is the type identity the host and
        // a plugin agree on. The plugin itself and Device Lab now live in their own repositories,
        // so this also catches either being pulled back in as a project rather than a pinned
        // release — which would rebuild the two-SDK conflict those splits exist to avoid.
        Assert.Contains("WSGM.Device.Sdk", references);
        Assert.DoesNotContain("WSGM.Device.Msi.Claw8A2Vm", references);
        Assert.DoesNotContain("WSGM.DeviceLab", references);
    }

    [Fact]
    public void DeviceSdkHasNoProjectOrPackageDependencies()
    {
        // The SDK lives in its own repository and guards this there too. It is re-checked from
        // here because the pin is what WSGM actually builds: a submodule moved to a revision that
        // acquired a dependency would hand every plugin that dependency, and this is the build
        // that would ship it.
        XDocument sdk = LoadProject(
            "external/WSGM.Device.Sdk/src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj");

        Assert.Empty(sdk.Descendants("ProjectReference"));
        Assert.Empty(sdk.Descendants("PackageReference"));
    }

    private static IEnumerable<string> ProjectReferences(string relativePath) =>
        LoadProject(relativePath)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFileNameWithoutExtension(path!));

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

            return Assert.IsType<DirectoryInfo>(directory).FullName;
        }
    }
}
