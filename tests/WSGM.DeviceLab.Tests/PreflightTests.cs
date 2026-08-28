using WSGM.DeviceLab.Core;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Tests;

/// <summary>Device Lab preflight and output firewall executable specifications.</summary>
public class PreflightTests
{
    [Fact]
    public void OutputPolicy_RejectsTheLiveDataTreeWithoutOpeningIt()
    {
        using TestDirectory test = new();
        DeviceLabPathBoundaries boundaries = Boundaries(test.Path);

        DeviceLabOutputPathDecision root = DeviceLabOutputPathPolicy.Evaluate(
            boundaries.LiveDataDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        DeviceLabOutputPathDecision child = DeviceLabOutputPathPolicy.Evaluate(
            Path.Combine(boundaries.LiveDataDirectory, "config.json"),
            DeviceLabOutputTargetKind.NewFile,
            boundaries);

        Assert.False(root.IsAllowed);
        Assert.False(child.IsAllowed);
        Assert.Equal(DeviceLabOutputPathRisk.LiveDataDirectory, root.Risk);
        Assert.Equal(DeviceLabOutputPathRisk.LiveDataDirectory, child.Risk);
        Assert.False(Directory.Exists(boundaries.LiveDataDirectory));
    }

    [Fact]
    public void OutputPolicy_RejectsBroadRootsAndAllowsSpecificNewChildren()
    {
        using TestDirectory test = new();
        DeviceLabPathBoundaries boundaries = Boundaries(test.Path);
        string home = Assert.Single(boundaries.BroadHomeDirectories);

        Assert.Equal(
            DeviceLabOutputPathRisk.BroadHomeDirectory,
            DeviceLabOutputPathPolicy.Evaluate(
                home,
                DeviceLabOutputTargetKind.Directory,
                boundaries).Risk);
        Assert.Equal(
            DeviceLabOutputPathRisk.RepositoryRoot,
            DeviceLabOutputPathPolicy.Evaluate(
                boundaries.RepositoryRoot,
                DeviceLabOutputTargetKind.Directory,
                boundaries).Risk);
        Assert.Equal(
            DeviceLabOutputPathRisk.DriveRoot,
            DeviceLabOutputPathPolicy.Evaluate(
                Path.GetPathRoot(test.Path),
                DeviceLabOutputTargetKind.Directory,
                boundaries).Risk);

        DeviceLabOutputPathDecision safe = DeviceLabOutputPathPolicy.Evaluate(
            Path.Combine(home, "wsgm-device", "capture"),
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        Assert.True(safe.IsAllowed);
    }

    [Fact]
    public void NewFilePolicy_RefusesEveryOverwriteShape()
    {
        using TestDirectory test = new();
        string existingFile = Path.Combine(test.Path, "existing.json");
        File.WriteAllText(existingFile, "original");
        string existingDirectory = Path.Combine(test.Path, "existing-directory");
        Directory.CreateDirectory(existingDirectory);
        DeviceLabPathBoundaries boundaries = Boundaries(test.Path);

        Assert.Equal(
            DeviceLabOutputPathRisk.ExistingTarget,
            DeviceLabOutputPathPolicy.Evaluate(
                existingFile,
                DeviceLabOutputTargetKind.NewFile,
                boundaries).Risk);
        Assert.Equal(
            DeviceLabOutputPathRisk.ExistingTarget,
            DeviceLabOutputPathPolicy.Evaluate(
                existingDirectory,
                DeviceLabOutputTargetKind.NewFile,
                boundaries).Risk);
        Assert.Equal("original", File.ReadAllText(existingFile));
    }

    [Fact]
    public void RepositoryLocator_FindsTheMarkerFromANestedOutputPath()
    {
        using TestDirectory test = new();
        File.WriteAllText(Path.Combine(test.Path, "WSGM.slnx"), "<Solution />");
        string nested = Path.Combine(test.Path, "src", "tool", "bin");
        Directory.CreateDirectory(nested);

        string? found = DeviceLabRepositoryLocator.Find(nested);

        Assert.Equal(test.Path, found, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_AllReadyEnvironment_IsPassAndSerializesDeterministically()
    {
        using TestDirectory test = new();
        DeviceLabOutputPathDecision output = AllowedOutput(test.Path);
        DeviceLabDoctorSnapshot snapshot = ReadySnapshot() with
        {
            RequiredApis =
            [
                Api("z-last", available: true),
                Api("a-first", available: true),
            ],
        };
        DateTimeOffset capturedAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

        DeviceLabDoctorReport first = DeviceLabDoctor.Evaluate(snapshot, output, capturedAt);
        DeviceLabDoctorReport second = DeviceLabDoctor.Evaluate(snapshot, output, capturedAt);

        Assert.Equal(DeviceLabDoctorStatus.Pass, first.Status);
        Assert.Equal(["api.a-first", "api.z-last"], first.Checks
            .Where(check => check.Category == "api")
            .Select(check => check.Code));
        Assert.Equal(DeviceLabJson.Serialize(first), DeviceLabJson.Serialize(second));
    }

    [Fact]
    public void Doctor_MissingRequiredApiAndUnsafeOutput_AreBlockedWhileOptionalRightsWarn()
    {
        using TestDirectory test = new();
        DeviceLabOutputPathDecision output = new()
        {
            IsAllowed = false,
            FullPath = Path.Combine(test.Path, "live"),
            Risk = DeviceLabOutputPathRisk.LiveDataDirectory,
            Reason = "protected",
        };
        DeviceLabDoctorSnapshot snapshot = ReadySnapshot() with
        {
            IsElevated = false,
            IsDeveloperModeEnabled = false,
            RequiredApis = [Api("required", available: false)],
        };

        DeviceLabDoctorReport report = DeviceLabDoctor.Evaluate(
            snapshot,
            output,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(DeviceLabDoctorStatus.Blocked, report.Status);
        Assert.Contains(report.Checks, check =>
            check.Code == "api.required" && check.Status == DeviceLabDoctorStatus.Blocked);
        Assert.Contains(report.Checks, check =>
            check.Code == "output.path" && check.Status == DeviceLabDoctorStatus.Blocked);
        Assert.Contains(report.Checks, check =>
            check.Code == "permissions.elevation" && check.Status == DeviceLabDoctorStatus.Warning);
        Assert.Contains(report.Checks, check =>
            check.Code == "developer-mode" && check.Status == DeviceLabDoctorStatus.Warning);
    }

    private static DeviceLabPathBoundaries Boundaries(string root) => new()
    {
        LiveDataDirectory = Path.Combine(root, "protected-live-data"),
        RepositoryRoot = Path.Combine(root, "repository"),
        BroadHomeDirectories = [Path.Combine(root, "profile")],
    };

    private static DeviceLabOutputPathDecision AllowedOutput(string root) => new()
    {
        IsAllowed = true,
        FullPath = Path.Combine(root, "output"),
        Risk = DeviceLabOutputPathRisk.None,
    };

    private static DeviceLabDoctorSnapshot ReadySnapshot() => new()
    {
        IsWindows = true,
        Is64BitOperatingSystem = true,
        Is64BitProcess = true,
        RuntimeMajorVersion = 10,
        RuntimeDescription = ".NET 10.0.0",
        RuntimeIdentifier = "win-x64",
        IsElevated = true,
        IsUserInteractive = true,
        IsContinuousIntegration = false,
        IsDeveloperModeEnabled = true,
        RequiredApis = [Api("required", available: true)],
        OutputPathWritable = true,
    };

    private static WindowsApiAvailability Api(string name, bool available) => new()
    {
        Name = name,
        Library = $"{name}.dll",
        Export = "RequiredExport",
        Available = available,
    };

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WSGM.DeviceLab.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            string tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            string resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to delete a test directory outside temp.");
            }

            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
