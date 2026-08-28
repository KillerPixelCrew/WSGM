using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Identity;

namespace WSGM.Core;

/// <summary>Atomically stages an already expanded, offline plugin package as a new immutable version.</summary>
internal static class DevicePackageStager
{
    private const int MaxFiles = 512;
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    internal static async Task<DevicePackageCandidate> StageAsync(
        string sourceDirectory,
        string destinationRoot,
        DevicePluginTrustTier trustTier,
        DeviceIdentitySnapshot identity,
        IDevicePackageSignatureVerifier signatureVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(signatureVerifier);
        if (trustTier is DevicePluginTrustTier.WsgmReviewed)
        {
            throw new InvalidOperationException(
                "WSGM-reviewed packages are installed only by the reviewed installer path.");
        }

        string source = Path.GetFullPath(sourceDirectory);
        string destination = Path.GetFullPath(destinationRoot);
        if (!Directory.Exists(source) || IsLink(source))
        {
            throw new InvalidDataException("Package source is absent or is a link/reparse point.");
        }

        InstalledDevicePackageRecord record = ReadRecord(
            Path.Combine(source, "installed.wsgm.json"));
        if (record.TrustTier != trustTier
            || !SafeSegment(record.PackageId)
            || !SafeSegment(record.Version))
        {
            throw new InvalidDataException("Package install identity or trust tier is invalid.");
        }

        Directory.CreateDirectory(destination);
        string stagingRoot = Path.Combine(destination, $".staging-{Guid.NewGuid():N}");
        string stagingVersion = Path.Combine(stagingRoot, record.PackageId, record.Version);
        string finalParent = Path.Combine(destination, record.PackageId);
        string finalVersion = Path.Combine(finalParent, record.Version);
        if (Directory.Exists(finalVersion))
        {
            throw new IOException("This immutable package version is already installed.");
        }

        try
        {
            Directory.CreateDirectory(stagingVersion);
            await CopyPackageAsync(source, stagingVersion, cancellationToken).ConfigureAwait(false);
            DevicePackageDiscoveryOptions options = OptionsForStaging(stagingRoot, trustTier);
            IReadOnlyList<DevicePackageCandidate> candidates = DevicePackagePolicy.Discover(
                options,
                identity,
                signatureVerifier);
            DevicePackageCandidate[] matching = candidates.Where(candidate =>
                string.Equals(candidate.Manifest?.Id, record.PackageId, StringComparison.Ordinal)
                && string.Equals(candidate.Manifest?.Version, record.Version, StringComparison.Ordinal))
                .ToArray();
            if (matching.Length != 1
                || matching[0].Manifest is null
                || (!matching[0].Eligible
                    && matching[0].RejectionCode is not "developer-never-auto-activates"))
            {
                string reason = matching.FirstOrDefault()?.RejectionCode
                    ?? "staged-package-validation-failed";
                throw new InvalidDataException($"Staged package validation failed: {reason}.");
            }

            Directory.CreateDirectory(finalParent);
            Directory.Move(stagingVersion, finalVersion);
            return matching[0] with { PackagePath = finalVersion };
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static async Task CopyPackageAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Stack<(string Source, string Destination)> pending = new();
        pending.Push((source, destination));
        int fileCount = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            (string currentSource, string currentDestination) = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(currentSource)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsLink(entry))
                {
                    throw new InvalidDataException("Package staging never follows links or reparse points.");
                }

                string target = Path.Combine(currentDestination, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    Directory.CreateDirectory(target);
                    pending.Push((entry, target));
                    continue;
                }

                FileInfo file = new(entry);
                fileCount++;
                totalBytes = checked(totalBytes + file.Length);
                if (fileCount > MaxFiles || file.Length > MaxFileBytes || totalBytes > MaxPackageBytes)
                {
                    throw new InvalidDataException("Package exceeds staging file or size bounds.");
                }

                await using FileStream input = new(
                    entry,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using FileStream output = new(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static InstalledDevicePackageRecord ReadRecord(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024)
        {
            throw new InvalidDataException("Install record exceeds 1 MiB.");
        }

        return JsonSerializer.Deserialize(
            stream,
            DevicePackageJsonContext.Default.InstalledDevicePackageRecord)
            ?? throw new InvalidDataException("Install record deserialized to null.");
    }

    private static DevicePackageDiscoveryOptions OptionsForStaging(
        string stagingRoot,
        DevicePluginTrustTier trustTier)
    {
        string absent = Path.Combine(stagingRoot, ".absent");
        return new DevicePackageDiscoveryOptions
        {
            ReviewedRoot = trustTier is DevicePluginTrustTier.WsgmReviewed ? stagingRoot : absent,
            SignedExternalRoot = trustTier is DevicePluginTrustTier.SignedExternal ? stagingRoot : absent,
            CommunityRoot = trustTier is DevicePluginTrustTier.SideloadedCommunity ? stagingRoot : absent,
            DeveloperRoot = trustTier is DevicePluginTrustTier.Developer ? stagingRoot : absent,
            DeveloperMode = trustTier is DevicePluginTrustTier.Developer,
        };
    }

    private static bool SafeSegment(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');

    private static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.Exists && (info.LinkTarget is not null
            || (info.Attributes & FileAttributes.ReparsePoint) != 0);
    }
}
