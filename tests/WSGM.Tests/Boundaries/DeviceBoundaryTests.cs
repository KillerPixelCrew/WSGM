using System.Xml.Linq;
using WSGM.Testing;

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
        Assert.DoesNotContain("WSGM.Device.Msi.Claw", references);
        Assert.DoesNotContain("WSGM.DeviceLab", references);
        Assert.DoesNotContain("WSGM.Device.HandheldCompanion", references);
    }

    [Fact]
    public void SolutionBuildsTheDeviceProjectsWithOneSharedSdk()
    {
        var projects = SolutionProjects().ToArray();

        Assert.Contains(
            "src/WSGM.DeviceLab/WSGM.DeviceLab.csproj",
            projects);
        Assert.Contains(
            "src/WSGM.Device.Msi.Claw/WSGM.Device.Msi.Claw.csproj",
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

    [Fact]
    public void ProjectsReferenceOnlyWhatTheirLayerAllows()
    {
        const string deviceSdk = "src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj";
        const string pluginSdk = "src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj";
        const string toolkit = "external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiToolkit.csproj";
        const string deviceControl =
            "external/windows-device-control/src/WindowsDeviceControl/WindowsDeviceControl.csproj";
        List<string> problems = [];
        foreach (var project in SolutionProjects())
        {
            var name = Path.GetFileNameWithoutExtension(project);
            string[]? allowed = IsTestProject(project)
                ? null
                : name switch
                {
                    // Reusable libraries and the small executables stand alone: the launchers and the
                    // logon service must not pull in WSGM, Avalonia or any other first-party assembly.
                    "WindowsDeviceControl" or "SteamUiToolkit" or "WSGM.Device.Sdk" or "WSGM.Launch"
                        or "WSGM.LogonService" or "WSGM.PackagedLaunch" => [],
                    "WSGM.Plugin.Sdk" => [toolkit, deviceSdk],
                    "WSGM.Plugin.NvidiaGpu" => [pluginSdk, deviceControl],
                    "WSGM.DeviceLab" => [deviceSdk],
                    "WSGM" =>
                    [
                        "src/Avalonia.LiveBackdrop/Avalonia.LiveBackdrop.csproj",
                        "src/WSGM.Install/WSGM.Install.csproj",
                        pluginSdk,
                        deviceSdk,
                        toolkit,
                        deviceControl
                    ],
                    _ when name.StartsWith("WSGM.Plugin.", StringComparison.Ordinal) => [pluginSdk],
                    _ when name.StartsWith("WSGM.Device.", StringComparison.Ordinal) => [deviceSdk],
                    _ => null
                };
            if (allowed is not null)
            {
                problems.AddRange(ResolvedProjectReferences(project)
                    .Where(reference => !allowed.Contains(reference, StringComparer.OrdinalIgnoreCase))
                    .Select(reference => $"{project} references {reference}"));
            }

            // A source compiled by more than one project lives under src/Shared, so an edit to it is
            // visibly an edit to every consumer; nothing links a file out of another project's folder.
            problems.AddRange(LinkedSources(project)
                .Where(source => !source.StartsWith("src/Shared/", StringComparison.OrdinalIgnoreCase)
                                 && !source.StartsWith("external/steam-input-lease/bindings/",
                                     StringComparison.OrdinalIgnoreCase)
                                 && !(IsTestProject(project)
                                      && source.StartsWith("tests/Shared/", StringComparison.OrdinalIgnoreCase))
                                 && !(project == "tests/WSGM.Plugin.Sdk.Tests/WSGM.Plugin.Sdk.Tests.csproj"
                                      && source.StartsWith("eng/templates/", StringComparison.OrdinalIgnoreCase)))
                .Select(source => $"{project} compiles {source}"));
        }

        string[] pluginSdkReferences = [toolkit, deviceSdk];
        Assert.Equal(pluginSdkReferences, ResolvedProjectReferences(pluginSdk).Order(StringComparer.Ordinal));
        Assert.Empty(problems);
    }

    [Fact]
    public void DeviceLabUsesTheProductLicenseWhileTheSdkContractsStayMit()
    {
        var lab = RepositoryFiles.LoadProject("src/WSGM.DeviceLab/WSGM.DeviceLab.csproj");
        Assert.Equal("GPL-3.0-or-later", Assert.Single(lab.Descendants("PackageLicenseExpression")).Value);
        var license = Assert.Single(lab.Descendants("EmbeddedResource"), resource =>
            (string?)resource.Attribute("LogicalName") == "WSGM.DeviceLab.Help.LICENSE");
        Assert.Equal("LICENSE", Assert.Single(Resolved("src/WSGM.DeviceLab/WSGM.DeviceLab.csproj", [license])));

        foreach (var project in new[] { "WSGM.Device.Sdk", "WSGM.Plugin.Sdk" })
        {
            var sdk = RepositoryFiles.LoadProject($"src/{project}/{project}.csproj");
            Assert.Equal("MIT", Assert.Single(sdk.Descendants("PackageLicenseExpression")).Value);
        }
    }

    [Fact]
    public void LauncherCopiesOfSharedTypesHaveNoGlobalAliasInTheMainTests()
    {
        var references = RepositoryFiles.LoadProject("tests/WSGM.Tests/WSGM.Tests.csproj")
            .Descendants("ProjectReference");
        foreach (var name in new[] { "WSGM.Launch", "WSGM.LogonService" })
        {
            var reference = Assert.Single(references, item =>
                Path.GetFileNameWithoutExtension((string)item.Attribute("Include")!) == name);
            var aliases = ((string?)reference.Attribute("Aliases"))?.Split(',') ?? [];
            Assert.NotEmpty(aliases);
            Assert.DoesNotContain("global", aliases);
        }
    }

    private static IEnumerable<string> SolutionProjects()
    {
        return XDocument.Load(Path.Combine(RepositoryFiles.Root, "WSGM.slnx"))
            .Descendants("Project")
            .Select(project => (string?)project.Attribute("Path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('\\', '/'));
    }

    private static bool IsTestProject(string project)
    {
        return project.StartsWith("tests/", StringComparison.Ordinal) || project.Contains("/tests/");
    }

    private static IEnumerable<string> ResolvedProjectReferences(string project)
    {
        return Resolved(project, RepositoryFiles.LoadProject(project).Descendants("ProjectReference"));
    }

    /// <summary>Compile items whose path leaves the project's own folder, repository-relative.</summary>
    private static IEnumerable<string> LinkedSources(string project)
    {
        var directory = Path.GetFullPath(Path.GetDirectoryName(Path.Combine(RepositoryFiles.Root, project))!);
        return Resolved(project, RepositoryFiles.LoadProject(project).Descendants("Compile"))
            .Where(source => !Path.GetFullPath(Path.Combine(RepositoryFiles.Root, source))
                .StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Resolved(string project, IEnumerable<XElement> items)
    {
        var directory = Path.GetFullPath(Path.GetDirectoryName(Path.Combine(RepositoryFiles.Root, project))!);
        return items
            .Select(item => (string?)item.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetRelativePath(
                    RepositoryFiles.Root,
                    Path.GetFullPath(Path.Combine(directory, include!.Replace('\\', Path.DirectorySeparatorChar))))
                .Replace('\\', '/'));
    }

    private static IEnumerable<string> ProjectReferences(string relativePath)
    {
        return RepositoryFiles.LoadProject(relativePath)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFileNameWithoutExtension(path!));
    }
}
