using System.Xml.Linq;
using WSGM.Device.Tests;

namespace WSGM.Tests.Boundaries;

public sealed class DeviceBoundaryTests
{
    [Fact]
    public void WsgmLoadsThePluginDynamicallyWithoutReferencingItsProject()
    {
        var references = ProjectReferences("src/WSGM/WSGM.csproj").ToArray();

        // The SDK is the only device reference the application may hold: it is the type identity
        // the host and a plugin agree on. The solution builds the tool and package from their
        // source projects, but the application still discovers the installed plugin dynamically.
        Assert.Contains("WSGM.Device.Sdk", references);
        Assert.DoesNotContain("WSGM.Device.Msi.Claw8A2Vm", references);
        Assert.DoesNotContain("WSGM.DeviceLab", references);
        Assert.DoesNotContain("WSGM.Device.HandheldCompanion", references);
    }

    [Fact]
    public void SolutionBuildsTheDeviceProjectsWithOneSharedSdk()
    {
        var projects = XDocument.Load(Path.Combine(RepositoryFiles.Root, "WSGM.slnx"))
            .Descendants("Project")
            .Select(project => (string?)project.Attribute("Path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('\\', '/'))
            .ToArray();

        Assert.Contains(
            "src/WSGM.DeviceLab/WSGM.DeviceLab.csproj",
            projects);
        Assert.Contains(
            "src/WSGM.Device.Msi.Claw8A2Vm/WSGM.Device.Msi.Claw8A2Vm.csproj",
            projects);
        Assert.Contains(
            "src/WSGM.Device.HandheldCompanion/WSGM.Device.HandheldCompanion.csproj",
            projects);
        Assert.Equal(
            "src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj",
            Assert.Single(projects, path => Path.GetFileName(path) == "WSGM.Device.Sdk.csproj"));

        var sdkPath = Path.GetFullPath(Path.Combine(
            RepositoryFiles.Root, "src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj"));
        foreach (var projectPath in projects)
        {
            var projectDirectory = Path.GetDirectoryName(Path.Combine(RepositoryFiles.Root, projectPath))!;
            foreach (var reference in RepositoryFiles.LoadProject(projectPath).Descendants("ProjectReference"))
            {
                var include = (string)reference.Attribute("Include")!;
                if (Path.GetFileName(include) == "WSGM.Device.Sdk.csproj")
                {
                    Assert.Equal(sdkPath, Path.GetFullPath(Path.Combine(projectDirectory, include)));
                }
            }
        }
    }

    [Fact]
    public void DeviceSdkHasNoProjectOrPackageDependencies()
    {
        // The SDK is the shared type-identity boundary. Any dependency added here would be
        // handed to every plugin built against it.
        var sdk = RepositoryFiles.LoadProject(
            "src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj");

        Assert.Empty(sdk.Descendants("ProjectReference"));
        Assert.Empty(sdk.Descendants("PackageReference"));
    }

    private static IEnumerable<string> ProjectReferences(string relativePath) =>
        RepositoryFiles.LoadProject(relativePath)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFileNameWithoutExtension(path!));
}
