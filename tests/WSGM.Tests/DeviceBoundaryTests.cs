using System.Xml.Linq;

namespace WSGM.Tests;

/// <summary>
/// Guards the WSGM 2.0 device-platform boundary at the project-reference level.
/// </summary>
/// <remarks>
/// WSGM stays NativeAOT and may reference exactly one new assembly, <c>WSGM.Device.Contracts</c>.
/// DeviceHost, the SDK, Device Lab, and every plugin stay JIT because they need
/// <c>System.Management</c>/WMI, WinRT sensors, and an interactive keyboard hook. Nothing enforces
/// that at compile time: adding the wrong reference produces a perfectly good build whose AOT publish
/// then fails, or worse, succeeds and drags reflection-dependent code into the shell.
/// <para>
/// These tests read the project files rather than loaded assemblies, so they fail on the reference
/// itself instead of waiting for a runtime symptom. The complementary output-directory check —
/// for a binary that arrives without a reference — is <c>eng\check-aot-isolation.ps1</c>.
/// </para>
/// </remarks>
public class DeviceBoundaryTests
{
    private static readonly string[] AotProjects =
    [
        "src/WSGM/WSGM.csproj",
        "src/WSGM.Launch/WSGM.Launch.csproj",
        "src/WSGM.LogonService/WSGM.LogonService.csproj",
    ];

    /// <summary>Assemblies that must never enter an AOT project's reference closure.</summary>
    private static readonly string[] ForbiddenInAotProjects =
    [
        "WSGM.DeviceHost",
        "WSGM.Device.Sdk",
        "WSGM.Device.ProbeHost",
        "WSGM.DeviceLab.Core",
        "WSGM.DeviceLab.Cli",
        "WSGM.Device.Msi.Claw8A2Vm",
        "System.Management",
        "Microsoft.Windows.SDK.NET",
        "HIDMaestro",
    ];

    [Fact]
    public void AotProjects_ReferenceNoDeviceRuntimeOrToolingAssembly()
    {
        List<string> violations = [];

        foreach (string project in AotProjects)
        {
            foreach (string reference in ReferencedNames(project))
            {
                if (ForbiddenInAotProjects.Any(f =>
                        reference.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{project} -> {reference}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "NativeAOT projects may reference only WSGM.Device.Contracts from the device platform. "
                + $"Found: {string.Join(", ", violations)}");
    }

    [Fact]
    public void WsgmExecutable_ReferencesOnlyContractsFromTheDevicePlatform()
    {
        string[] devicePlatformReferences = ReferencedNames("src/WSGM/WSGM.csproj")
            .Where(r => r.StartsWith("WSGM.Device", StringComparison.OrdinalIgnoreCase)
                || r.StartsWith("WSGM.DeviceLab", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.All(
            devicePlatformReferences,
            reference => Assert.Equal("WSGM.Device.Contracts", reference));
    }

    [Fact]
    public void NoProjectReferencesAPluginPackage()
    {
        List<string> violations = [];

        foreach (string project in AllProjectFiles())
        {
            foreach (XElement reference in ProjectReferences(project))
            {
                string include = (reference.Attribute("Include")?.Value ?? string.Empty)
                    .Replace('\\', '/');
                if (include.Contains("/plugins/", StringComparison.OrdinalIgnoreCase)
                    || include.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{project} -> {include}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "A plugin package is loaded at runtime by DeviceHost from its package directory and is "
                + $"never referenced. Found: {string.Join(", ", violations)}");
    }

    [Fact]
    public void ContractsProjectIsMarkedAotCompatible()
    {
        XDocument contracts = XDocument.Load(
            Path.Combine(RepositoryRoot, "src/WSGM.Device.Contracts/WSGM.Device.Contracts.csproj"));

        string? aotCompatible = contracts
            .Descendants("IsAotCompatible")
            .Select(e => e.Value)
            .FirstOrDefault();

        Assert.Equal("true", aotCompatible, ignoreCase: true);
    }

    [Fact]
    public void ContractsProjectReferencesNothing()
    {
        // The contract assembly is compiled into WSGM's AOT image. A dependency here is a dependency
        // of the shell, so the reference set stays empty rather than "carefully chosen".
        Assert.Empty(ProjectReferences("src/WSGM.Device.Contracts/WSGM.Device.Contracts.csproj"));
        Assert.Empty(PackageReferences("src/WSGM.Device.Contracts/WSGM.Device.Contracts.csproj"));
    }

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

            Assert.NotNull(directory);
            return directory.FullName;
        }
    }

    private static IEnumerable<string> AllProjectFiles()
    {
        string root = RepositoryRoot;
        return Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}third_party{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'));
    }

    private static List<XElement> ProjectReferences(string relativeProjectPath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativeProjectPath))
            .Descendants("ProjectReference")
            .ToList();

    private static List<XElement> PackageReferences(string relativeProjectPath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativeProjectPath))
            .Descendants("PackageReference")
            .ToList();

    /// <summary>
    /// Returns the assembly names a project pulls in directly, from both project and package
    /// references. Transitive project references are followed so a reference laundered through an
    /// intermediate project is still caught.
    /// </summary>
    private static IEnumerable<string> ReferencedNames(string relativeProjectPath)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        List<string> names = [];
        Collect(relativeProjectPath);
        return names;

        void Collect(string projectPath)
        {
            string fullPath = Path.GetFullPath(Path.Combine(RepositoryRoot, projectPath));
            if (!visited.Add(fullPath) || !File.Exists(fullPath))
            {
                return;
            }

            XDocument document = XDocument.Load(fullPath);
            string? projectDirectory = Path.GetDirectoryName(fullPath);

            foreach (XElement package in document.Descendants("PackageReference"))
            {
                string? include = package.Attribute("Include")?.Value;
                if (!string.IsNullOrEmpty(include))
                {
                    names.Add(include);
                }
            }

            foreach (XElement project in document.Descendants("ProjectReference"))
            {
                string? include = project.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(include) || projectDirectory is null)
                {
                    continue;
                }

                names.Add(Path.GetFileNameWithoutExtension(include));
                Collect(Path.GetFullPath(Path.Combine(projectDirectory, include)));
            }
        }
    }
}
