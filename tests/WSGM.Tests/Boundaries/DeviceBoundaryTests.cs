using System.Xml.Linq;
using WSGM.Testing;

namespace WSGM.Tests.Boundaries;

public sealed class DeviceBoundaryTests
{
    [Fact]
    public void WsgmUsesTheHandheldLibraryAndSinglePluginSdkDirectly()
    {
        var references = ProjectReferences("src/WSGM/WSGM.csproj").ToArray();

        Assert.Contains("LibHandheld", references);
        Assert.Contains("LibGPUDriverInteract", references);
        Assert.Contains("WSGM.Plugin.Sdk", references);
        Assert.DoesNotContain(references, reference => reference.StartsWith("WSGM.Device.", StringComparison.Ordinal));
        Assert.DoesNotContain("WSGM.DeviceLab", references);
    }

    [Fact]
    public void SolutionRetiresDevicePackagesAndTheirSeparateSdk()
    {
        var projects = SolutionProjects().ToArray();

        Assert.Contains("src/WSGM.DeviceLab/WSGM.DeviceLab.csproj", projects);
        Assert.Contains("external/libhandheld/src/LibHandheld/LibHandheld.csproj", projects);
        Assert.Equal("src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj",
            Assert.Single(projects, path => Path.GetFileName(path) == "WSGM.Plugin.Sdk.csproj"));
        Assert.DoesNotContain(projects, project =>
            Path.GetFileName(project).StartsWith("WSGM.Device.", StringComparison.Ordinal));
    }

    [Fact]
    public void HandheldLibraryIsIndependentOfWsgmProjects()
    {
        var library = RepositoryFiles.LoadProject("external/libhandheld/src/LibHandheld/LibHandheld.csproj");

        Assert.Empty(library.Descendants("ProjectReference"));
        var package = Assert.Single(library.Descendants("PackageReference"));
        Assert.Equal("System.Management", (string?)package.Attribute("Include"));
        Assert.Equal("10.0.12", (string?)package.Attribute("Version"));
    }

    [Fact]
    public void ProjectsReferenceOnlyWhatTheirLayerAllows()
    {
        const string pluginSdk = "src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj";
        const string toolkit = "external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiToolkit.csproj";
        const string deviceControl =
            "external/windows-device-control/src/WindowsDeviceControl/WindowsDeviceControl.csproj";
        const string gpuLibrary =
            "external/libgpu-driver-interact/src/LibGPUDriverInteract/LibGPUDriverInteract.csproj";
        const string handheldLibrary = "external/libhandheld/src/LibHandheld/LibHandheld.csproj";
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
                    "WindowsDeviceControl" or "SteamUiToolkit" or "LibHandheld" or "WSGM.Launch"
                        or "WSGM.LogonService" or "WSGM.PackagedLaunch" => [],
                    "WSGM.Plugin.Sdk" => [toolkit],
                    "LibGPUDriverInteract" =>
                    [
                        deviceControl,
                        "external/libgpu-driver-interact/external/windows-device-control/src/WindowsDeviceControl/WindowsDeviceControl.csproj"
                    ],
                    "WSGM.DeviceLab" => [pluginSdk],
                    "WSGM.Install" => [pluginSdk, handheldLibrary],
                    "WSGM" =>
                    [
                        "src/Avalonia.LiveBackdrop/Avalonia.LiveBackdrop.csproj",
                        "src/WSGM.Install/WSGM.Install.csproj",
                        pluginSdk,
                        handheldLibrary,
                        toolkit,
                        deviceControl,
                        gpuLibrary
                    ],
                    _ when name.StartsWith("WSGM.Plugin.", StringComparison.Ordinal) => [pluginSdk],
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

        string[] pluginSdkReferences = [toolkit];
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

        var sdk = RepositoryFiles.LoadProject("src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj");
        Assert.Equal("MIT", Assert.Single(sdk.Descendants("PackageLicenseExpression")).Value);
        Assert.Equal("0.4.0", Assert.Single(sdk.Descendants("Version")).Value);
        var handheld = RepositoryFiles.LoadProject("external/libhandheld/src/LibHandheld/LibHandheld.csproj");
        Assert.Equal("MIT", Assert.Single(handheld.Descendants("PackageLicenseExpression")).Value);
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
        var document = RepositoryFiles.LoadProject(project);
        var references = document.Descendants("ProjectReference").Select(reference => new XElement(reference))
            .ToArray();
        foreach (var reference in references.Where(reference =>
                     (string?)reference.Attribute("Include") == "$(WindowsDeviceControlProject)"))
        {
            var paths = document.Descendants("WindowsDeviceControlProject").Select(property => property.Value)
                .ToArray();
            Assert.NotEmpty(paths);
            Assert.All(paths, path => Assert.EndsWith("/src/WindowsDeviceControl/WindowsDeviceControl.csproj", path));
            reference.SetAttributeValue("Include", paths.First(path => File.Exists(Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Path.Combine(RepositoryFiles.Root, project))!, path)))));
        }

        return Resolved(project, references);
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
