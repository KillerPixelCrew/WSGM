using System.IO.Compression;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Tests;
using WSGM.DeviceLab.Packaging;
using WSGM.DeviceLab.Tests.Builders;

namespace WSGM.DeviceLab.Tests.Packaging;

public sealed class DeviceLabPackagingTests
{
    [Fact]
    public void Pack_ValidMinimalPackage_ProducesDeterministicArchiveContainingOnlyPackageFiles()
    {
        using TemporaryDirectory temporary = new();
        var source = CreatePackage(temporary);
        var boundaries = DeviceLabPackages.Boundaries(temporary);
        var first = temporary.GetPath("first.wsgmpkg");
        var second = temporary.GetPath("second.wsgmpkg");

        var validated = PluginPackageWorkflow.ValidateOffline(source);
        var firstReport = PluginPackageWorkflow.Pack(
            source,
            first,
            boundaries);
        var secondReport = PluginPackageWorkflow.Pack(
            source,
            second,
            boundaries);

        Assert.True(validated.Valid, Describe(validated));
        Assert.True(firstReport.Valid, Describe(firstReport));
        Assert.True(secondReport.Valid, Describe(secondReport));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));

        using var archive = ZipFile.OpenRead(first);
        var entries = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("plugin.wsgm.json", entries);
        Assert.Contains("Synthetic.Dock.dll", entries);
        Assert.Equal(2, entries.Length);
    }

    [Fact]
    public void ValidateOffline_PrivilegedProvisioningArtifact_IsRejected()
    {
        using TemporaryDirectory temporary = new();
        var source = CreatePackage(temporary);
        File.WriteAllText(Path.Combine(source, "install-driver.ps1"), "exit 0");

        var report = PluginPackageWorkflow.ValidateOffline(source);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "forbidden-provisioning-artifact");
    }

    [Fact]
    public void ValidateOffline_OversizedManifest_IsRejectedBeforeReadingThePayload()
    {
        using TemporaryDirectory temporary = new();
        var source = temporary.GetPath("package");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(
            Path.Combine(source, "plugin.wsgm.json"),
            new byte[ManifestLimits.MaxDocumentBytes + 1]);

        var report = PluginPackageWorkflow.ValidateOffline(source);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "document-too-large");
    }

    [Fact]
    public void ValidateOffline_NonX64EntryAssembly_IsRejectedBeforePacking()
    {
        using TemporaryDirectory temporary = new();
        var source = CreatePackage(temporary);
        WriteManagedPe(Path.Combine(source, "Synthetic.Dock.dll"), machine: 0x014c);

        var report = PluginPackageWorkflow.ValidateOffline(source);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "architecture-unsupported");
    }

    [Fact]
    public void ValidateOffline_TruncatedAmd64HeaderIsRejectedAsMalformed()
    {
        using TemporaryDirectory temporary = new();
        var source = CreatePackage(temporary);
        WriteTruncatedPeHeader(Path.Combine(source, "Synthetic.Dock.dll"), machine: 0x8664);

        var report = PluginPackageWorkflow.ValidateOffline(source);

        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "architecture-unsupported");
    }

    [Fact]
    public void BoundedEntryCapture_StopsAfterOneOverflowObservationBeforeSorting()
    {
        var observed = 0;
        var accepted = DeviceLabPackageSnapshot.TakeBoundedEntries(
            Entries(),
            remaining: 4,
            CancellationToken.None,
            out var exceeded);

        Assert.True(exceeded);
        Assert.Equal(4, accepted.Count);
        Assert.Equal(5, observed);

        return;

        IEnumerable<string> Entries()
        {
            // Unbounded in practice: the capture must stop on its own after one overflow entry.
            while (observed < int.MaxValue)
            {
                observed++;
                yield return $"entry-{observed}";
            }
        }
    }

    [Fact]
    public void Pack_PinsValidatedSourceBytesAgainstReplacementUntilArchivePublication()
    {
        using TemporaryDirectory temporary = new();
        var source = CreatePackage(temporary);
        var entryAssembly = Path.Combine(source, "Synthetic.Dock.dll");
        var output = temporary.GetPath("pinned.wsgmpkg");
        var replacementBlocked = false;

        var report = PluginPackageWorkflow.Pack(
            source,
            output,
            DeviceLabPackages.Boundaries(temporary),
            sourceValidated: () =>
            {
                _ = Assert.Throws<IOException>(() => File.WriteAllBytes(entryAssembly, [1, 2, 3]));
                replacementBlocked = true;
            },
            CancellationToken.None);

        Assert.True(report.Valid, Describe(report));
        Assert.True(replacementBlocked);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Pack_CancellationAfterValidationPublishesNoArchive()
    {
        using TemporaryDirectory temporary = new();
        var source = CreatePackage(temporary);
        var output = temporary.GetPath("cancelled.wsgmpkg");
        using CancellationTokenSource cancellation = new();

        _ = Assert.Throws<OperationCanceledException>(() => PluginPackageWorkflow.Pack(
            source,
            output,
            DeviceLabPackages.Boundaries(temporary),
            cancellation.Cancel,
            cancellation.Token));

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(temporary.Root, "cancelled.wsgmpkg.*.tmp"));
    }

    [Fact]
    public void PackageBudget_RejectsFileCountSingleFileAndAggregateOverflow()
    {
        Assert.True(PluginPackageWorkflow.PackageEntryBudgetExceeded(
            PluginPackageWorkflow.MaximumPackageEntries));
        Assert.False(PluginPackageWorkflow.PackageEntryBudgetExceeded(
            PluginPackageWorkflow.MaximumPackageEntries - 1));
        Assert.Equal(
            "package-too-many-files",
            PluginPackageWorkflow.PackageBudgetViolation(
                PluginPackageWorkflow.MaximumPackageFiles,
                acceptedBytes: 0,
                nextFileBytes: 0));
        Assert.Equal(
            "file-too-large",
            PluginPackageWorkflow.PackageBudgetViolation(
                acceptedFileCount: 0,
                acceptedBytes: 0,
                nextFileBytes: PluginPackageWorkflow.MaximumPackageFileBytes + 1));
        Assert.Equal(
            "package-too-large",
            PluginPackageWorkflow.PackageBudgetViolation(
                acceptedFileCount: 1,
                acceptedBytes: PluginPackageWorkflow.MaximumPackageBytes,
                nextFileBytes: 1));
    }

    private static string CreatePackage(TemporaryDirectory temporary)
    {
        var source = temporary.GetPath("package");
        Directory.CreateDirectory(source);
        WriteManagedPe(Path.Combine(source, "Synthetic.Dock.dll"));
        File.WriteAllBytes(
            Path.Combine(source, "plugin.wsgm.json"),
            PluginManifestFixture.Serialize(PluginManifestFixture.Manifest()));
        return source;
    }

    private static void WriteManagedPe(string path, ushort? machine = null)
    {
        var bytes = File.ReadAllBytes(typeof(DeviceLabPackagingTests).Assembly.Location);
        var peOffset = BitConverter.ToInt32(bytes, 60);
        if (machine is { } patchedMachine)
        {
            BitConverter.GetBytes(patchedMachine).CopyTo(bytes, peOffset + 4);
        }
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteTruncatedPeHeader(string path, ushort machine)
    {
        var bytes = new byte[70];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(bytes, 60);
        bytes[64] = (byte)'P';
        bytes[65] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 68);
        File.WriteAllBytes(path, bytes);
    }

    private static string Describe(PluginPackageValidationReport report) =>
        string.Join("; ", report.Issues.Select(issue => $"{issue.Path}: {issue.Message}"));
}
