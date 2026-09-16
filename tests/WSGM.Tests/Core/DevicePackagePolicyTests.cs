using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Packaging;
using WSGM.Device.Sdk.Serialization;
using WSGM.Interop;

namespace WSGM.Tests.Core;

public sealed class DevicePackagePolicyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wsgm-package-slot-").FullName;

    [Fact]
    public void EmptySlot_StartsWithoutADevicePackage()
    {
        var inventory = DevicePackagePolicy.Inventory(_root);
        var discovery = Discover();

        Assert.Equal(DevicePackageCardinality.Empty, inventory.Cardinality);
        Assert.Null(discovery.InstalledPackage);
        Assert.Null(discovery.ErrorCode);
    }

    [Fact]
    public void ProtectedSlotInspectionFailure_IsNotTreatedAsAnEmptySlot()
    {
        Assert.Throws<UnauthorizedAccessException>(() => DevicePackagePolicy.Inventory(
            _root,
            _ => throw new UnauthorizedAccessException("simulated slot refusal")));
    }

    [Fact]
    public void PackageRootThatDisappearsFromTheEnumeratedSnapshot_FailsClosed()
    {
        var packagePath = Directory.CreateDirectory(Path.Combine(_root, "vanished")).FullName;

        Assert.Throws<IOException>(() => DevicePackagePolicy.Inventory(
            _root,
            path => string.Equals(path, packagePath, StringComparison.OrdinalIgnoreCase)
                ? null
                : ReadAttributesExactly(path)));
    }

    [Fact]
    public void OneValidPackage_IsTheOnlyEligibleCandidate()
    {
        CreatePackage("valid");

        var inventory = DevicePackagePolicy.Inventory(_root);
        var package = Assert.IsType<InstalledDevicePackage>(
            Discover().InstalledPackage);

        Assert.Equal(DevicePackageCardinality.Single, inventory.Cardinality);
        Assert.True(package.Valid);
        Assert.Equal("valid", package.Manifest?.Id);
    }

    [Fact]
    public void TruncatedAmd64Header_IsRejectedAsNotManagedAssembly()
    {
        var packagePath = CreatePackage("truncated-pe");
        WriteTruncatedAmd64Header(Path.Combine(packagePath, "plugin.dll"));

        var package = Assert.IsType<InstalledDevicePackage>(
            Discover().InstalledPackage);

        Assert.False(package.Valid);
        Assert.Equal("architecture-unsupported", package.RejectionCode);
    }

    [Fact]
    public void PackageDiscovery_TotalEntryBoundCountsDirectoriesBeforeValidation()
    {
        var packagePath = CreatePackage("entry-bound");
        for (var index = 0; index < 1023; index++)
        {
            Directory.CreateDirectory(Path.Combine(packagePath, $"directory-{index:D4}"));
        }

        var package = Assert.IsType<InstalledDevicePackage>(
            Discover().InstalledPackage);

        Assert.False(package.Valid);
        Assert.Equal("package-invalid", package.RejectionCode);
        Assert.Contains(
            "filesystem-entry",
            Assert.IsType<string>(package.Detail),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneMalformedPackage_ReportsTheDeviceErrorWithoutSelectingIt()
    {
        var packagePath = Directory.CreateDirectory(Path.Combine(_root, "malformed")).FullName;
        File.WriteAllText(Path.Combine(packagePath, "plugin.wsgm.json"), "not-json", Encoding.UTF8);

        var package = Assert.IsType<InstalledDevicePackage>(
            Discover().InstalledPackage);

        Assert.False(package.Valid);
        Assert.Equal("manifest-invalid", package.RejectionCode);
    }

    [Fact]
    public void OneApiIncompatiblePackage_ReportsTheExactVersionMismatch()
    {
        CreatePackage("future", apiVersion: DeviceApi.Version + 1);

        var package = Assert.IsType<InstalledDevicePackage>(
            Discover().InstalledPackage);

        Assert.False(package.Valid);
        Assert.Equal("api-incompatible", package.RejectionCode);
    }

    [Fact]
    public void TwoValidRoots_AreRefusedBeforeEitherManifestIsRead()
    {
        var first = CreatePackage("first");
        var second = CreatePackage("second");
        File.WriteAllText(Path.Combine(first, "plugin.wsgm.json"), "broken", Encoding.UTF8);
        File.WriteAllText(Path.Combine(second, "plugin.wsgm.json"), "also-broken", Encoding.UTF8);

        var discovery = Discover();

        Assert.Equal(2, discovery.Inventory.PackageRoots.Count);
        Assert.Null(discovery.InstalledPackage);
        Assert.Equal("multiple-package-roots", discovery.ErrorCode);
    }

    [Fact]
    public void ValidAndMalformedRoots_AreBothRefusedByCardinality()
    {
        CreatePackage("valid");
        Directory.CreateDirectory(Path.Combine(_root, "malformed"));

        var inventory = DevicePackagePolicy.Inventory(_root);
        var discovery = Discover();

        Assert.Equal(DevicePackageCardinality.Multiple, inventory.Cardinality);
        Assert.Equal(2, discovery.Inventory.PackageRoots.Count);
        Assert.Null(discovery.InstalledPackage);
        Assert.Equal("multiple-package-roots", discovery.ErrorCode);
    }

    [Fact]
    public async Task PackageUpdate_ReplacesTheWholeSlotAndLeavesOneRoot()
    {
        var installed = Path.Combine(_root, "installed");
        Directory.CreateDirectory(installed);
        var oldRoot = CreatePackage("old", installed);
        var sourceParent = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var source = CreatePackage("new", sourceParent);

        var installedPackage = await DevicePackageStager.StageAsync(
            source,
            installed);

        var inventory = DevicePackagePolicy.Inventory(installed);
        Assert.Equal(DevicePackageCardinality.Single, inventory.Cardinality);
        Assert.Equal("new", Path.GetFileName(Assert.Single(inventory.PackageRoots)));
        Assert.Equal(Path.Combine(installed, "new"), installedPackage.PackagePath);
        Assert.False(Directory.Exists(oldRoot));
    }

    [Fact]
    public async Task PackageUpdate_NormalizesTrailingSourceAndSlotSeparators()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("old", installed);
        var sourceParent = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var source = CreatePackage("new", sourceParent);

        var installedPackage = await DevicePackageStager.StageAsync(
            source + Path.DirectorySeparatorChar,
            installed + Path.DirectorySeparatorChar);

        Assert.Equal(Path.Combine(installed, "new"), installedPackage.PackagePath);
        Assert.False(Directory.Exists(Path.Combine(installed, ".staging")));
        Assert.False(Directory.Exists(Path.Combine(installed, ".previous")));
        Assert.True(Directory.Exists(Path.Combine(installed, "new")));
    }

    [Fact]
    public async Task PackageUpdate_ReplacesAnAmbiguousSlotWithExactlyOneRoot()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("old-a", installed);
        CreatePackage("old-b", installed);
        var sourceParent = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var source = CreatePackage("new", sourceParent);

        await DevicePackageStager.StageAsync(source, installed);

        var inventory = DevicePackagePolicy.Inventory(installed);
        Assert.Equal(DevicePackageCardinality.Single, inventory.Cardinality);
        Assert.Equal("new", Path.GetFileName(Assert.Single(inventory.PackageRoots)));
    }

    [Fact]
    public async Task PackageUpdate_InvalidReplacementPreservesTheExistingSlot()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        var oldRoot = CreatePackage("old", installed);
        var source = CreatePackage("invalid-source");
        File.WriteAllBytes(Path.Combine(source, "plugin.dll"), [0, 1, 2, 3]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevicePackageStager.StageAsync(source, installed));

        Assert.True(Directory.Exists(oldRoot));
        Assert.Equal("old", Path.GetFileName(Assert.Single(
            DevicePackagePolicy.Inventory(installed).PackageRoots)));
        Assert.False(Directory.Exists(DevicePackageStager.ReplacementStagingRoot(installed)));
    }

    [Fact]
    public async Task PackageUpdate_MoveFailureRestoresThePreviousSlotAndClearsRecoveryState()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        var oldRoot = CreatePackage("old", installed);
        var sourceParent = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var source = CreatePackage("new", sourceParent);

        await Assert.ThrowsAsync<IOException>(() => DevicePackageStager.StageAsync(
            source,
            installed,
            previousSlotMoved: static () => throw new IOException("simulated publish failure")));

        Assert.True(Directory.Exists(oldRoot));
        Assert.False(Directory.Exists(DevicePackageStager.ReplacementRecoveryRoot(installed)));
        Assert.False(Directory.Exists(DevicePackageStager.ReplacementStagingRoot(installed)));
    }

    [Fact]
    public async Task PackageUpdate_CallerCancellationAfterParkingRestoresThePreviousSlot()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        var oldRoot = CreatePackage("old", installed);
        var sourceParent = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var source = CreatePackage("new", sourceParent);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevicePackageStager.StageAsync(
            source,
            installed,
            cancellation.Cancel,
            cancellationToken: cancellation.Token));

        Assert.True(Directory.Exists(oldRoot));
        Assert.False(Directory.Exists(DevicePackageStager.ReplacementRecoveryRoot(installed)));
        Assert.False(Directory.Exists(DevicePackageStager.ReplacementStagingRoot(installed)));
    }

    [Fact]
    public void PackageUpdate_CrashAfterParkingThePreviousSlotRestoresItDuringReconciliation()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("old", installed);
        var recovery = DevicePackageStager.ReplacementRecoveryRoot(installed);
        Directory.Move(installed, recovery);

        DevicePackageStager.ReconcileInstalledPackage(installed);

        Assert.True(Directory.Exists(Path.Combine(installed, "old")));
        Assert.False(Directory.Exists(recovery));
    }

    [Fact]
    public async Task PackageUpdate_ReconcilesAParkedPreviousSlotBeforeRejectingTheSource()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("old", installed);
        var recovery = DevicePackageStager.ReplacementRecoveryRoot(installed);
        Directory.Move(installed, recovery);

        await Assert.ThrowsAsync<InvalidDataException>(() => DevicePackageStager.StageAsync(
            Path.Combine(_root, "missing-source"),
            installed));

        Assert.True(Directory.Exists(Path.Combine(installed, "old")));
        Assert.False(Directory.Exists(recovery));
    }

    [Fact]
    public void PackageUpdate_CrashAfterPublishingReplacementRetiresPreviousSlotDuringReconciliation()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("old", installed);
        var recovery = DevicePackageStager.ReplacementRecoveryRoot(installed);
        Directory.Move(installed, recovery);
        Directory.CreateDirectory(installed);
        CreatePackage("new", installed);

        DevicePackageStager.ReconcileInstalledPackage(installed);

        Assert.True(Directory.Exists(Path.Combine(installed, "new")));
        Assert.False(Directory.Exists(Path.Combine(installed, "old")));
        Assert.False(Directory.Exists(recovery));
    }

    [Fact]
    public void PackageUpdate_UsesTheInstallerCanonicalSiblingNames()
    {
        var installed = Path.Combine(_root, "installed");
        var installedWithSeparator = installed + Path.DirectorySeparatorChar;

        Assert.Equal(@"Global\WSGM.DevicePackageSlot", DevicePackageSlotGate.ProductionName);
        Assert.Equal(Path.Combine(_root, ".staging"),
            DevicePackageStager.ReplacementStagingRoot(installed));
        Assert.Equal(Path.Combine(_root, ".previous"),
            DevicePackageStager.ReplacementRecoveryRoot(installed));
        Assert.Equal(Path.Combine(_root, ".staging"),
            DevicePackageStager.ReplacementStagingRoot(installedWithSeparator));
        Assert.Equal(Path.Combine(_root, ".previous"),
            DevicePackageStager.ReplacementRecoveryRoot(installedWithSeparator));
    }

    [Fact]
    public void PackageUpdate_ReconciliationRemovesTheDeterministicStagingRoot()
    {
        var installed = Path.Combine(_root, "installed");
        var staging = Directory.CreateDirectory(
            DevicePackageStager.ReplacementStagingRoot(installed)).FullName;
        File.WriteAllText(Path.Combine(staging, "partial"), "partial");

        DevicePackageStager.ReconcileInstalledPackage(installed);

        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData("installed")]
    [InlineData(".previous")]
    [InlineData(".staging")]
    [InlineData("parent")]
    public void PackageUpdate_AttributeAccessFailurePreventsAnyRecoveryOrCleanupMutation(
        string inaccessiblePath)
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        var staging = Directory.CreateDirectory(
            DevicePackageStager.ReplacementStagingRoot(installed)).FullName;
        string[] protectedRoots = [installed, recovery, staging];
        foreach (var protectedRoot in protectedRoots)
        {
            File.WriteAllText(Path.Combine(protectedRoot, "must-survive"), protectedRoot);
        }

        var inaccessible = string.Equals(inaccessiblePath, "parent", StringComparison.Ordinal)
            ? _root
            : Path.Combine(_root, inaccessiblePath);

        Assert.Throws<UnauthorizedAccessException>(() =>
            DevicePackageStager.ReconcileInstalledPackage(
                installed,
                path => string.Equals(path, inaccessible, StringComparison.OrdinalIgnoreCase)
                    ? throw new UnauthorizedAccessException("simulated protected-path refusal")
                    : ReadAttributesExactly(path)));

        Assert.All(protectedRoots, protectedRoot =>
            Assert.True(File.Exists(Path.Combine(protectedRoot, "must-survive"))));
    }

    [Fact]
    public async Task PackageUpdate_ParentInspectionFailurePreventsProtectedNamespaceCreation()
    {
        var sourceParent = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var source = CreatePackage("package", sourceParent);
        var protectedParent = Path.Combine(_root, "protected");
        var installed = Path.Combine(protectedParent, "installed");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            DevicePackageStager.StageAsync(
                source,
                installed,
                protectedPathAttributeReader: path => string.Equals(
                    path,
                    protectedParent,
                    StringComparison.OrdinalIgnoreCase)
                    ? throw new UnauthorizedAccessException("simulated parent refusal")
                    : ReadAttributesExactly(path)));

        Assert.False(Directory.Exists(protectedParent));
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public async Task PackageUpdate_RejectsAPathThatOverlapsTheInstalledSlot()
    {
        var sourceParent = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var source = CreatePackage("new", sourceParent);
        var nestedDestination = Path.Combine(source, "installed");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevicePackageStager.StageAsync(source, nestedDestination));

        Assert.Contains("separate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(nestedDestination));

        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        var nestedSource = CreatePackage("nested-source", installed);
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        await File.WriteAllTextAsync(Path.Combine(recovery, "must-survive"), "recovery");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevicePackageStager.StageAsync(nestedSource, installed));
        Assert.True(Directory.Exists(nestedSource));
        Assert.True(File.Exists(Path.Combine(recovery, "must-survive")));
    }

    [Theory]
    [InlineData(".staging")]
    [InlineData(".previous")]
    public async Task PackageUpdate_RejectsReservedSiblingSourcesBeforeReconciliation(
        string reservedSibling)
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("live", installed);
        var reservedRoot = Directory.CreateDirectory(
            Path.Combine(_root, reservedSibling)).FullName;
        var source = CreatePackage("source", reservedRoot);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevicePackageStager.StageAsync(source, installed));

        Assert.Contains("staging or recovery", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(Path.Combine(installed, "live")));
    }

    [Fact]
    public async Task PackageUpdate_RejectsAReparseAliasedSourceBeforeReconciliation()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("live", installed);
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        await File.WriteAllTextAsync(Path.Combine(recovery, "must-survive"), "recovery");
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(_root, "external-source")).FullName;
        var source = CreatePackage("source", sourceParent);
        var inspected = false;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevicePackageStager.StageAsync(
                source,
                installed,
                sourcePathTraversesLink: _ =>
                {
                    inspected = true;
                    return true;
                }));

        Assert.True(inspected);
        Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(recovery, "must-survive")));
        Assert.True(Directory.Exists(Path.Combine(installed, "live")));
    }

    [Fact]
    public async Task PackageUpdate_RejectsMissingReparseAliasedSourceBeforeReconciliation()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("live", installed);
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        await File.WriteAllTextAsync(Path.Combine(recovery, "must-survive"), "recovery");
        var missingSource = Path.Combine(_root, "missing-link", "package");
        var inspected = false;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevicePackageStager.StageAsync(
                missingSource,
                installed,
                sourcePathTraversesLink: _ =>
                {
                    inspected = true;
                    return true;
                }));

        Assert.True(inspected);
        Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(recovery, "must-survive")));
        Assert.True(Directory.Exists(Path.Combine(installed, "live")));
    }

    [Fact]
    public async Task PackageUpdate_RejectsFileIdentityAliasedSourceBeforeReconciliation()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("live", installed);
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        await File.WriteAllTextAsync(Path.Combine(recovery, "must-survive"), "recovery");
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(_root, "alternate-spelling")).FullName;
        var source = CreatePackage("source", sourceParent);
        NativePathIdentity aliasedIdentity = new(42, 84);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevicePackageStager.StageAsync(
                source,
                installed,
                pathIdentityReader: path =>
                    string.Equals(path, source, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(path, recovery, StringComparison.OrdinalIgnoreCase)
                        ? aliasedIdentity
                        : null));

        Assert.Contains("staging or recovery", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(recovery, "must-survive")));
        Assert.True(Directory.Exists(Path.Combine(installed, "live")));
    }

    [Fact]
    public async Task PackageUpdate_RejectsSourceIdentityRaceAfterNoFollowHandlesAreAcquired()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("live", installed);
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        await File.WriteAllTextAsync(Path.Combine(recovery, "must-survive"), "recovery");
        var sourceParent = Directory.CreateDirectory(
            Path.Combine(_root, "identity-race")).FullName;
        var source = CreatePackage("source", sourceParent);
        var originalIdentity = NativePathIdentityReader.Read(source)
                               ?? throw new InvalidOperationException("The source identity was not available.");
        var sourceSecured = false;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevicePackageStager.StageAsync(
                source,
                installed,
                sourceRootSecured: () => sourceSecured = true,
                securedSourceIdentityReader: _ => sourceSecured
                    ? originalIdentity with { FileId = originalIdentity.FileId ^ 1 }
                    : originalIdentity));

        Assert.True(sourceSecured);
        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(recovery, "must-survive")));
        Assert.True(Directory.Exists(Path.Combine(installed, "live")));
    }

    [Fact]
    public void PackageSource_NoFollowHandlesBlockRootAndFileReplacementUntilDisposed()
    {
        var source = CreatePackage("locked-source");
        var movedSource = Path.Combine(_root, "moved-source");
        var plugin = Path.Combine(source, "plugin.dll");
        var movedPlugin = Path.Combine(source, "moved-plugin.dll");
        using var packageSource = Assert.IsType<NativePackageSource>(
            NativePackageSource.TryOpen(source));
        using var sourceEntry = packageSource.OpenEntry(plugin);

        Assert.Throws<IOException>(() => Directory.Move(source, movedSource));
        Assert.Throws<IOException>(() => File.Move(plugin, movedPlugin));

        Assert.True(Directory.Exists(source));
        Assert.True(File.Exists(plugin));
    }

    [Fact]
    public async Task PackageUpdate_TotalEntryBoundCountsDirectoriesBeforeStagingThem()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("live", installed);
        var source = CreatePackage("entry-bound-source");
        for (var index = 0; index < 1023; index++)
        {
            Directory.CreateDirectory(Path.Combine(source, $"directory-{index:D4}"));
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            DevicePackageStager.StageAsync(source, installed));

        // The entry ceiling, not the size ceiling: directories cost an entry and no bytes.
        Assert.Contains("filesystem-entry limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(installed, "live")));
        Assert.False(Directory.Exists(DevicePackageStager.ReplacementStagingRoot(installed)));
    }

    [Fact]
    public void PackageRemoval_RemovesTheWholeSlotAndIsIdempotent()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        CreatePackage("old-a", installed);
        CreatePackage("old-b", installed);

        DevicePackageStager.RemoveInstalledPackage(installed);
        DevicePackageStager.RemoveInstalledPackage(installed);

        Assert.False(Directory.Exists(installed));
    }

    [Fact]
    public void PackageRemoval_RetiresLiveRecoveryAndStagingNamespacesWithoutResurrection()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        var staging = Directory.CreateDirectory(
            DevicePackageStager.ReplacementStagingRoot(installed)).FullName;
        CreatePackage("live", installed);
        CreatePackage("recovery", recovery);

        DevicePackageStager.RemoveInstalledPackage(installed);
        DevicePackageStager.ReconcileInstalledPackage(installed);

        Assert.False(Directory.Exists(installed));
        Assert.False(Directory.Exists(recovery));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void PackageRemoval_StagingInspectionIoFailurePreventsEveryNamespaceMutation()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        var staging = Directory.CreateDirectory(
            DevicePackageStager.ReplacementStagingRoot(installed)).FullName;
        string[] protectedRoots = [installed, recovery, staging];
        foreach (var protectedRoot in protectedRoots)
        {
            File.WriteAllText(Path.Combine(protectedRoot, "must-survive"), protectedRoot);
        }

        Assert.Throws<IOException>(() => DevicePackageStager.RemoveInstalledPackage(
            installed,
            attributeReader: path => string.Equals(
                path,
                staging,
                StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("simulated protected-path IO failure")
                : ReadAttributesExactly(path)));

        Assert.All(protectedRoots, protectedRoot =>
            Assert.True(File.Exists(Path.Combine(protectedRoot, "must-survive"))));
    }

    [Fact]
    public void PackageRemoval_RecoveryCleanupFailureLeavesTheLiveSlotInPlace()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        CreatePackage("live", installed);
        CreatePackage("recovery", recovery);

        Assert.Throws<IOException>(() => DevicePackageStager.RemoveInstalledPackage(
            installed,
            path =>
            {
                if (string.Equals(path, recovery, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("simulated recovery cleanup failure");
                }
            }));

        Assert.True(Directory.Exists(Path.Combine(installed, "live")));
        Assert.True(Directory.Exists(recovery));
        Assert.Equal(DevicePackageCardinality.Single,
            DevicePackageStager.InventoryEffectiveInstalledPackage(installed).Cardinality);
    }

    [Fact]
    public async Task PackageSlotGate_ExcludesConcurrentStartupAndMaintenanceOwners()
    {
        var name = $@"Local\WSGM.Tests.DevicePackageSlot.{Guid.NewGuid():N}";
        var first = Assert.IsType<DevicePackageSlotGate>(
            await DevicePackageSlotGate.TryAcquireAsync(name, TimeSpan.Zero));
        await using (first)
        {
            Assert.Null(await DevicePackageSlotGate.TryAcquireAsync(name, TimeSpan.Zero));
        }

        await using var reacquired = Assert.IsType<DevicePackageSlotGate>(
            await DevicePackageSlotGate.TryAcquireAsync(name, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task PackageSlotGate_RecoversWhenTheOwningProcessExitsWithoutReleasing()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\WSGM.Tests.DevicePackageSlot.Crash.{suffix}";
        var readyName = $@"Local\WSGM.Tests.DevicePackageSlot.Ready.{suffix}";
        var exitName = $@"Local\WSGM.Tests.DevicePackageSlot.Exit.{suffix}";
        using var ready = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            readyName);
        using var exit = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            exitName);
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            $"$mutex=[Threading.Mutex]::new($false,'{mutexName}');"
                + "$null=$mutex.WaitOne();"
                + $"$ready=[Threading.EventWaitHandle]::OpenExisting('{readyName}');"
                + $"$exit=[Threading.EventWaitHandle]::OpenExisting('{exitName}');"
                + "$null=$ready.Set();"
                + "$null=$exit.WaitOne();"
                + "[Environment]::Exit(23)");

        using var holder = Process.Start(startInfo)
                           ?? throw new InvalidOperationException("Could not start the mutex-holder test process.");
        Task<DevicePackageSlotGate?>? recovery = null;
        DevicePackageSlotGate? recovered = null;
        Exception? testFailure = null;
        try
        {
            Assert.True(ready.WaitOne(TimeSpan.FromSeconds(10)));
            var waitStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            recovery = DevicePackageSlotGate.TryAcquireAsync(
                mutexName,
                TimeSpan.FromSeconds(10),
                waitStarted: () => waitStarted.TrySetResult());
            await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(holder.HasExited);

            Assert.True(exit.Set());
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(23, holder.ExitCode);

            recovered = Assert.IsType<DevicePackageSlotGate>(
                await recovery.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                _ = exit.Set();
                if (!holder.HasExited)
                {
                    try
                    {
                        await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch (TimeoutException)
                    {
                        if (!holder.HasExited)
                        {
                            holder.Kill(entireProcessTree: true);
                        }
                        await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            if (recovery is not null)
            {
                try
                {
                    recovered ??= await recovery.WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    cleanupFailure ??= ex;
                }
            }

            if (recovered is not null)
            {
                try
                {
                    await recovered.DisposeAsync();
                }
                catch (Exception ex)
                {
                    cleanupFailure ??= ex;
                }
            }

            if (testFailure is null && cleanupFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }
    }

    [Fact]
    public async Task PackageSlotGate_CallerCancellationIsPropagatedAndDoesNotKeepOwnership()
    {
        var name = $@"Local\WSGM.Tests.DevicePackageSlot.Cancel.{Guid.NewGuid():N}";
        var first = Assert.IsType<DevicePackageSlotGate>(
            await DevicePackageSlotGate.TryAcquireAsync(name, TimeSpan.Zero));
        await using (first)
        {
            using var cancellation = new CancellationTokenSource();
            var waitStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = DevicePackageSlotGate.TryAcquireAsync(
                name,
                TimeSpan.FromSeconds(10),
                () => waitStarted.TrySetResult(),
                cancellation.Token);
            await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await waiting.ConfigureAwait(false));
        }

        await using var reacquired = Assert.IsType<DevicePackageSlotGate>(
            await DevicePackageSlotGate.TryAcquireAsync(name, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task StartupInventory_WaitsForPackageSlotReplacementBeforeReading()
    {
        var installed = Path.Combine(_root, "installed");
        var name = $@"Local\WSGM.Tests.DevicePackageSlot.Inventory.{Guid.NewGuid():N}";
        var maintenance = Assert.IsType<DevicePackageSlotGate>(
            await DevicePackageSlotGate.TryAcquireAsync(name, TimeSpan.Zero));
        var waitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inventoryTask = Task.Run(() =>
            DevicePackageSlotGate.TryRunSynchronously(
                name,
                TimeSpan.FromSeconds(10),
                () => DevicePackageStager.InventoryEffectiveInstalledPackage(installed),
                () => waitStarted.TrySetResult()));
        DevicePackageInventory? inventory = null;
        Exception? testFailure = null;
        try
        {
            await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(inventoryTask.IsCompleted);
            CreatePackage("replacement", installed);
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                await maintenance.DisposeAsync();
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            try
            {
                inventory = await inventoryTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
            }

            if (testFailure is null && cleanupFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }

        var completedInventory = Assert.IsType<DevicePackageInventory>(inventory);
        Assert.Equal(DevicePackageCardinality.Single, completedInventory.Cardinality);
        Assert.Single(completedInventory.PackageRoots);
    }

    [Fact]
    public void StartupInventory_RefusesMultiplePackagesParkedInTheEffectiveRecoverySlot()
    {
        var installed = Path.Combine(_root, "installed");
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        var first = CreatePackage("first", recovery);
        var second = CreatePackage("second", recovery);
        var gateName = $@"Local\WSGM.Tests.DevicePackageSlot.RecoveryInventory.{Guid.NewGuid():N}";

        var inventory = Assert.IsType<DevicePackageInventory>(
            DevicePackageSlotGate.TryRunSynchronously(
                gateName,
                TimeSpan.Zero,
                () => DevicePackageStager.InventoryEffectiveInstalledPackage(installed)));

        Assert.Equal(DevicePackageCardinality.Multiple, inventory.Cardinality);
        Assert.Equal(
            new[] { first, second }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            inventory.PackageRoots.ToArray());
        Assert.False(Directory.Exists(installed));
        Assert.True(Directory.Exists(recovery));
    }

    [Fact]
    public void StartupInventory_JudgesTheParkedSlotWhenTheLiveSlotIsAbsent()
    {
        // An interrupted swap left the packages parked. Startup inventories the slot that would
        // become active after recovery, so an ambiguous parked set is refused before any UI runs
        // rather than after reconciliation has already moved it into place.
        var installed = Path.Combine(_root, "installed");
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        var first = CreatePackage("first", recovery);
        var second = CreatePackage("second", recovery);

        var inventory = DevicePackageStager.InventoryEffectiveInstalledPackage(
            installed);

        Assert.Equal(DevicePackageCardinality.Multiple, inventory.Cardinality);
        Assert.Equal(
            new[] { first, second }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            inventory.PackageRoots.ToArray());
    }

    [Fact]
    public void StartupInventory_RecoveryInspectionFailureIsNotTreatedAsAnAbsentSlot()
    {
        var installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
        var recovery = Directory.CreateDirectory(
            DevicePackageStager.ReplacementRecoveryRoot(installed)).FullName;
        File.WriteAllText(Path.Combine(installed, "must-survive"), "live");
        File.WriteAllText(Path.Combine(recovery, "must-survive"), "recovery");

        Assert.Throws<UnauthorizedAccessException>(() =>
            DevicePackageStager.InventoryEffectiveInstalledPackage(
                installed,
                path => string.Equals(path, recovery, StringComparison.OrdinalIgnoreCase)
                    ? throw new UnauthorizedAccessException("simulated recovery refusal")
                    : ReadAttributesExactly(path)));

        Assert.True(File.Exists(Path.Combine(installed, "must-survive")));
        Assert.True(File.Exists(Path.Combine(recovery, "must-survive")));
    }

    [Fact]
    public void PackageSlotInspectionFailures_FailClosedForMutexIoAndRecoveryErrors()
    {
        Assert.True(Program.IsDevicePackageSlotGateFailure(new IOException("filesystem")));
        Assert.True(Program.IsDevicePackageSlotGateFailure(
            new UnauthorizedAccessException("access")));
        Assert.True(Program.IsDevicePackageSlotGateFailure(
            new InvalidDataException("ambiguous recovery slot")));
        Assert.True(Program.IsDevicePackageSlotGateFailure(
            new WaitHandleCannotBeOpenedException("mutex")));
        Assert.False(Program.IsDevicePackageSlotGateFailure(
            new InvalidOperationException("programming error")));
    }

    [Fact]
    public void DevicePluginMaintenanceParser_RequiresOneExactExclusiveCommand()
    {
        Assert.Equal(
            DevicePluginMaintenanceMode.Install,
            Program.ParseDevicePluginMaintenance(
                ["--install-device-plugin", "C:\\expanded package"]));
        Assert.Equal(
            DevicePluginMaintenanceMode.Remove,
            Program.ParseDevicePluginMaintenance(["--remove-device-plugin"]));
        Assert.Equal(
            DevicePluginMaintenanceMode.None,
            Program.ParseDevicePluginMaintenance(["--settings"]));

        string[][] invalid =
        [
            ["--install-device-plugin"],
            ["--install-device-plugin", " "],
            ["--install-device-plugin", "--setup"],
            ["--remove-device-plugin", "unexpected"],
            ["--settings", "--install-device-plugin", "C:\\expanded"],
            ["--install-device-plugin", "--remove-device-plugin"],
            ["--install-device-plugin", "C:\\expanded", "--settings"]
        ];
        Assert.All(invalid, arguments => Assert.Equal(
            DevicePluginMaintenanceMode.Invalid,
            Program.ParseDevicePluginMaintenance(arguments)));
    }

    [Theory]
    [InlineData("--restore-shell")]
    [InlineData("--setup")]
    [InlineData("--install-device-plugin")]
    [InlineData("--remove-device-plugin")]
    [InlineData("--overlay-test")]
    public void RecoveryAndMaintenanceModes_BypassTheStartupCardinalityRefusal(string argument)
    {
        Assert.False(Program.ShouldEnforceDevicePackageCardinality([argument]));
        Assert.True(Program.ShouldEnforceDevicePackageCardinality(["--settings"]));
    }

    [Theory]
    [InlineData("--shell")]
    [InlineData("--settings")]
    [InlineData("--boot")]
    public void OverlayTestBypass_DoesNotApplyToMixedRealStartupModes(string realMode)
    {
        Assert.True(Program.ShouldEnforceDevicePackageCardinality(
            [realMode, "--overlay-test"]));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private DevicePackageDiscovery Discover() => DevicePackagePolicy.Discover(_root);

    private string CreatePackage(string id, string? parent = null, int? apiVersion = null)
    {
        var package = Directory.CreateDirectory(Path.Combine(parent ?? _root, id)).FullName;
        PluginManifest manifest = new()
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            ApiVersion = apiVersion ?? DeviceApi.Version,
            EntryAssembly = "plugin.dll",
            EntryType = "Fixtures.Plugin"
        };
        File.WriteAllBytes(
            Path.Combine(package, "plugin.wsgm.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                DeviceJsonContext.Default.PluginManifest));
        File.Copy(
            typeof(DevicePackagePolicyTests).Assembly.Location,
            Path.Combine(package, "plugin.dll"));
        return package;
    }

    private static void WriteTruncatedAmd64Header(string path)
    {
        var bytes = new byte[70];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(bytes, 60);
        bytes[64] = (byte)'P';
        bytes[65] = (byte)'E';
        bytes[68] = 0x64;
        bytes[69] = 0x86;
        File.WriteAllBytes(path, bytes);
    }

    private static FileAttributes? ReadAttributesExactly(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
