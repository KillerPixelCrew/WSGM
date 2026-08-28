using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Packaging;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>Runtime trust tier assigned by the installer or an explicit user workflow.</summary>
public enum DevicePluginTrustTier
{
    /// <summary>Built and installed by the WSGM release process.</summary>
    WsgmReviewed,

    /// <summary>Authenticode publisher explicitly approved by the user.</summary>
    SignedExternal,

    /// <summary>Permanently labelled unreviewed manual package.</summary>
    SideloadedCommunity,

    /// <summary>Device Lab source build; never automatically selected.</summary>
    Developer,
}

/// <summary>Installer-owned immutable grant accompanying one expanded package version.</summary>
public sealed record InstalledDevicePackageRecord
{
    /// <summary>Record schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Package identifier expected in the manifest.</summary>
    public required string PackageId { get; init; }

    /// <summary>Installed package version.</summary>
    public required string Version { get; init; }

    /// <summary>Tier assigned by the installation route.</summary>
    public required DevicePluginTrustTier TrustTier { get; init; }

    /// <summary>Whether the user explicitly enabled this package.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Certificate subject pinned at first install, when signatures are required.</summary>
    public string? PublisherSubject { get; init; }

    /// <summary>Certificate thumbprint pinned at first install.</summary>
    public string? PublisherThumbprint { get; init; }

    /// <summary>SHA-256 for every package file except this record.</summary>
    public IReadOnlyDictionary<string, string> FileHashes { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>When this immutable version was installed.</summary>
    public required DateTimeOffset InstalledAt { get; init; }
}

/// <summary>One accepted or rejected runtime package candidate.</summary>
public sealed record DevicePackageCandidate
{
    /// <summary>Canonical expanded package directory.</summary>
    public required string PackagePath { get; init; }

    /// <summary>Trust tier fixed by the discovery root.</summary>
    public required DevicePluginTrustTier TrustTier { get; init; }

    /// <summary>Parsed manifest when structural validation succeeded.</summary>
    public PluginManifest? Manifest { get; init; }

    /// <summary>Exact matched device definition when identity gates passed.</summary>
    public DeviceDefinition? MatchedDevice { get; init; }

    /// <summary>Count of satisfied required identity predicates, used for tie-breaking.</summary>
    public int Specificity { get; init; }

    /// <summary>Whether this candidate may be activated.</summary>
    public required bool Eligible { get; init; }

    /// <summary>Stable rejection code, or null when eligible.</summary>
    public string? RejectionCode { get; init; }

    /// <summary>Sanitized diagnostic detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>Configured administrator and per-user discovery roots.</summary>
public sealed record DevicePackageDiscoveryOptions
{
    /// <summary>Administrator-protected WSGM-reviewed root.</summary>
    public required string ReviewedRoot { get; init; }

    /// <summary>Per-user signed-external root.</summary>
    public required string SignedExternalRoot { get; init; }

    /// <summary>Per-user community root.</summary>
    public required string CommunityRoot { get; init; }

    /// <summary>Per-user Device Lab developer root.</summary>
    public required string DeveloperRoot { get; init; }

    /// <summary>Current runtime semantic API version.</summary>
    public int RuntimeApiVersion { get; init; } = 1;

    /// <summary>Whether Developer Mode permits developer candidates to be shown.</summary>
    public bool DeveloperMode { get; init; }

    /// <summary>Builds production roots without creating them.</summary>
    public static DevicePackageDiscoveryOptions Production(bool developerMode)
    {
        string baseDirectory = AppContext.BaseDirectory;
        string localRoot = Path.Combine(Log.Directory, "DevicePlugins");
        return new DevicePackageDiscoveryOptions
        {
            ReviewedRoot = Path.Combine(baseDirectory, "DevicePlugins", "reviewed"),
            SignedExternalRoot = Path.Combine(localRoot, "signed"),
            CommunityRoot = Path.Combine(localRoot, "community"),
            DeveloperRoot = Path.Combine(localRoot, "developer"),
            DeveloperMode = developerMode,
        };
    }
}

/// <summary>Publisher identity extracted after successful Authenticode verification.</summary>
public sealed record DevicePackagePublisher(string Subject, string Thumbprint);

/// <summary>Injectable signature verifier for deterministic policy tests.</summary>
public interface IDevicePackageSignatureVerifier
{
    /// <summary>Verifies one executable package entry point and returns its publisher.</summary>
    bool TryVerify(string path, out DevicePackagePublisher? publisher, out string detail);
}

/// <summary>Windows Authenticode verification with chain, timestamp, and revocation checking.</summary>
public sealed class WindowsDevicePackageSignatureVerifier : IDevicePackageSignatureVerifier
{
    /// <inheritdoc />
    public bool TryVerify(
        string path,
        out DevicePackagePublisher? publisher,
        out string detail)
    {
        publisher = null;
        int status = NativeAuthenticode.VerifyFile(path);
        if (status != 0)
        {
            detail = $"WinVerifyTrust rejected the entry point (0x{status:X8}).";
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057 // Authenticode signer extraction has no replacement API.
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
            using X509Certificate2 certificate2 = new(certificate);
#pragma warning restore SYSLIB0057
            publisher = new DevicePackagePublisher(
                certificate2.Subject,
                certificate2.Thumbprint);
            detail = "Authenticode signature and publisher verified.";
            return true;
        }
        catch (CryptographicException ex)
        {
            detail = $"Signer extraction failed: {ex.Message}";
            return false;
        }
    }
}

/// <summary>Read-only package discovery, integrity validation, matching, and deterministic selection.</summary>
public static class DevicePackagePolicy
{
    private const string InstallRecordName = "installed.wsgm.json";
    private const string ManifestName = "plugin.wsgm.json";
    private const int MaxMetadataBytes = 1024 * 1024;
    private const int MaxPackageFiles = 512;
    private const long MaxPackageFileBytes = 128L * 1024 * 1024;
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    /// <summary>Discovers and validates every installed candidate without loading plugin code.</summary>
    public static IReadOnlyList<DevicePackageCandidate> Discover(
        DevicePackageDiscoveryOptions options,
        DeviceIdentitySnapshot identity,
        IDevicePackageSignatureVerifier? signatureVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);
        signatureVerifier ??= new WindowsDevicePackageSignatureVerifier();

        List<DevicePackageCandidate> candidates = [];
        DiscoverRoot(options.ReviewedRoot, DevicePluginTrustTier.WsgmReviewed, options, identity,
            signatureVerifier, candidates);
        DiscoverRoot(options.SignedExternalRoot, DevicePluginTrustTier.SignedExternal, options, identity,
            signatureVerifier, candidates);
        DiscoverRoot(options.CommunityRoot, DevicePluginTrustTier.SideloadedCommunity, options, identity,
            signatureVerifier, candidates);
        DiscoverRoot(options.DeveloperRoot, DevicePluginTrustTier.Developer, options, identity,
            signatureVerifier, candidates);
        return candidates.OrderBy(candidate => candidate.PackagePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Selects at most one package under the frozen four-key policy.</summary>
    public static DevicePackageCandidate? Select(
        IReadOnlyList<DevicePackageCandidate> candidates,
        string? explicitPackageId,
        out string? refusal) =>
        Select(
            candidates,
            string.IsNullOrWhiteSpace(explicitPackageId)
                ? null
                : new DevicePackageSelection { PackageId = explicitPackageId },
            out refusal);

    /// <summary>Selects at most one package, honoring an exact user version pin when present.</summary>
    public static DevicePackageCandidate? Select(
        IReadOnlyList<DevicePackageCandidate> candidates,
        DevicePackageSelection? explicitSelection,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        refusal = null;
        IEnumerable<DevicePackageCandidate> eligible = candidates.Where(candidate => candidate.Eligible);
        if (!string.IsNullOrWhiteSpace(explicitSelection?.PackageId))
        {
            DevicePackageCandidate[] explicitMatches = eligible.Where(candidate => string.Equals(
                candidate.Manifest?.Id,
                explicitSelection.PackageId,
                StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(explicitSelection.Version)
                    || string.Equals(
                        candidate.Manifest?.Version,
                        explicitSelection.Version,
                        StringComparison.Ordinal))).ToArray();
            if (explicitMatches.Length > 1)
            {
                explicitMatches = explicitMatches
                    .OrderBy(candidate => TrustRank(candidate.TrustTier))
                    .ThenByDescending(candidate => ParseVersion(candidate.Manifest?.Version))
                    .ToArray();
                if (explicitMatches[0].TrustTier == explicitMatches[1].TrustTier
                    && ParseVersion(explicitMatches[0].Manifest?.Version)
                        == ParseVersion(explicitMatches[1].Manifest?.Version))
                {
                    refusal = "ambiguous-package-selection";
                    return null;
                }

                return explicitMatches[0];
            }

            if (explicitMatches.Length == 1)
            {
                return explicitMatches[0];
            }

            refusal = explicitMatches.Length == 0
                ? "selected-package-unavailable"
                : "ambiguous-package-selection";
            return null;
        }

        DevicePackageCandidate[] ordered = eligible
            .OrderBy(candidate => TrustRank(candidate.TrustTier))
            .ThenByDescending(candidate => candidate.Specificity)
            .ThenByDescending(candidate => ParseVersion(candidate.Manifest?.Version))
            .ThenBy(candidate => candidate.Manifest?.Id, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
        {
            refusal = "no-matching-package";
            return null;
        }

        if (ordered.Length > 1 && SameSelectionKeys(ordered[0], ordered[1]))
        {
            refusal = "ambiguous-package-selection";
            return null;
        }

        return ordered[0];
    }

    private static void DiscoverRoot(
        string root,
        DevicePluginTrustTier trustTier,
        DevicePackageDiscoveryOptions options,
        DeviceIdentitySnapshot identity,
        IDevicePackageSignatureVerifier signatureVerifier,
        ICollection<DevicePackageCandidate> output)
    {
        if (!Directory.Exists(root) || IsLink(root))
        {
            return;
        }

        foreach (string packageIdDirectory in Directory.EnumerateDirectories(root)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (IsLink(packageIdDirectory))
            {
                output.Add(Reject(packageIdDirectory, trustTier, "package-link",
                    "Package discovery does not traverse links or reparse points."));
                continue;
            }

            foreach (string versionDirectory in Directory.EnumerateDirectories(packageIdDirectory)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                output.Add(ValidateCandidate(
                    versionDirectory,
                    trustTier,
                    options,
                    identity,
                    signatureVerifier));
            }
        }
    }

    private static DevicePackageCandidate ValidateCandidate(
        string packagePath,
        DevicePluginTrustTier trustTier,
        DevicePackageDiscoveryOptions options,
        DeviceIdentitySnapshot identity,
        IDevicePackageSignatureVerifier signatureVerifier)
    {
        try
        {
            string root = Path.GetFullPath(packagePath);
            if (IsLink(root))
            {
                return Reject(root, trustTier, "package-link", "Package version directory is a link.");
            }

            InstalledDevicePackageRecord record = ReadInstallRecord(Path.Combine(root, InstallRecordName));
            if (record.SchemaVersion != 1 || record.TrustTier != trustTier || !record.Enabled)
            {
                return Reject(root, trustTier, "install-grant-invalid",
                    "Install record schema, tier, or enabled state is invalid.");
            }

            if (trustTier is DevicePluginTrustTier.Developer && !options.DeveloperMode)
            {
                return Reject(root, trustTier, "developer-mode-disabled",
                    "Developer packages require Device Lab Developer Mode.");
            }

            VerifyIntegrity(root, record);
            byte[] manifestBytes = ReadAllBytesBounded(
                Constrain(root, ManifestName),
                MaxMetadataBytes,
                "Plugin manifest");
            PluginManifestReadResult manifestRead = PluginManifestReader.Read(manifestBytes);
            if (!manifestRead.IsValid || manifestRead.Manifest is null)
            {
                return Reject(root, trustTier, "manifest-invalid",
                    string.Join("; ", manifestRead.Errors.Select(error => error.Message)));
            }

            PluginManifest manifest = manifestRead.Manifest;
            if (!string.Equals(manifest.Id, record.PackageId, StringComparison.Ordinal)
                || !string.Equals(manifest.Version, record.Version, StringComparison.Ordinal))
            {
                return Reject(root, trustTier, "install-grant-confusion",
                    "Manifest identity differs from the installer-owned record.", manifest);
            }

            if (options.RuntimeApiVersion < manifest.MinApiVersion
                || options.RuntimeApiVersion > manifest.MaxApiVersion)
            {
                return Reject(root, trustTier, "api-incompatible",
                    "Package API window does not include this runtime.", manifest);
            }

            string entryPath = Constrain(root, manifest.EntryPoint);
            if (!IsX64Pe(entryPath))
            {
                return Reject(root, trustTier, "architecture-unsupported",
                    "Plugin entry point is not an x64 PE image.", manifest);
            }

            if (trustTier is DevicePluginTrustTier.WsgmReviewed
                or DevicePluginTrustTier.SignedExternal)
            {
                if (!signatureVerifier.TryVerify(entryPath, out DevicePackagePublisher? publisher,
                    out string signatureDetail) || publisher is null)
                {
                    return Reject(root, trustTier, "package-signature-invalid", signatureDetail, manifest);
                }

                if (!string.Equals(record.PublisherSubject, publisher.Subject, StringComparison.Ordinal)
                    || !string.Equals(record.PublisherThumbprint, publisher.Thumbprint,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(manifest.Publisher, publisher.Subject, StringComparison.Ordinal))
                {
                    return Reject(root, trustTier, "package-publisher-changed",
                        "Pinned, declared, and verified publisher identities differ.", manifest);
                }
            }

            (DeviceDefinition Device, IdentityMatchResult Match)[] matches = manifest.Devices
                .Select(device => (Device: device, Match: IdentityMatcher.Match(device, identity)))
                .Where(candidate => candidate.Match.Outcome is IdentityMatchOutcome.Matched)
                .ToArray();
            if (matches.Length != 1)
            {
                return Reject(root, trustTier,
                    matches.Length == 0 ? "identity-mismatch" : "ambiguous-device-definition",
                    $"Exact identity matching produced {matches.Length} definitions.", manifest);
            }

            int specificity = matches[0].Match.Explanations.Count(explanation =>
                explanation.Strength is IdentityStrength.Required && explanation.Satisfied);
            return new DevicePackageCandidate
            {
                PackagePath = root,
                TrustTier = trustTier,
                Manifest = manifest,
                MatchedDevice = matches[0].Device,
                Specificity = specificity,
                Eligible = trustTier is not DevicePluginTrustTier.Developer,
                RejectionCode = trustTier is DevicePluginTrustTier.Developer
                    ? "developer-never-auto-activates"
                    : null,
                Detail = trustTier is DevicePluginTrustTier.SideloadedCommunity
                    ? "Unreviewed community code; ordinary user integrity only."
                    : null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or JsonException or CryptographicException)
        {
            return Reject(packagePath, trustTier, "package-invalid", ex.Message);
        }
    }

    private static InstalledDevicePackageRecord ReadInstallRecord(string path)
    {
        byte[] bytes = ReadAllBytesBounded(path, MaxMetadataBytes, "Install record");

        return JsonSerializer.Deserialize(
            bytes,
            DevicePackageJsonContext.Default.InstalledDevicePackageRecord)
            ?? throw new InvalidDataException("Install record deserialized to null.");
    }

    private static void VerifyIntegrity(string root, InstalledDevicePackageRecord record)
    {
        string[] files = EnumeratePackageFiles(root)
            .Where(path => !string.Equals(Path.GetFileName(path), InstallRecordName,
                StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string[] declared = record.FileHashes.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!files.SequenceEqual(declared, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Install record does not cover the exact package file set.");
        }

        long totalBytes = 0;
        foreach ((string relativePath, string expectedHash) in record.FileHashes)
        {
            string path = Constrain(root, relativePath);
            if (IsLink(path))
            {
                throw new InvalidDataException("Package files may not be links or reparse points.");
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > MaxPackageFileBytes
                || checked(totalBytes + stream.Length) > MaxPackageBytes)
            {
                throw new InvalidDataException("Package exceeds the bounded integrity-check size.");
            }

            totalBytes += stream.Length;
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Package file hash mismatch: {relativePath}.");
            }
        }
    }

    private static IReadOnlyList<string> EnumeratePackageFiles(string root)
    {
        List<string> files = [];
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (IsLink(entry))
                {
                    throw new InvalidDataException("Package paths may not traverse links.");
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                files.Add(entry);
                if (files.Count > MaxPackageFiles)
                {
                    throw new InvalidDataException("Package contains too many files.");
                }
            }
        }

        return files;
    }

    private static byte[] ReadAllBytesBounded(string path, int maxBytes, string description)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException($"{description} exceeds {maxBytes} bytes.");
        }

        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string Constrain(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Package paths must be relative.");
        }

        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A package path escaped its immutable version directory.");
        }

        string current = root;
        foreach (string segment in Path.GetRelativePath(root, candidate).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && IsLink(current))
            {
                throw new InvalidDataException("Package paths may not traverse links.");
            }
        }

        return candidate;
    }

    private static bool IsX64Pe(string path)
    {
        using FileStream stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[64];
        if (stream.Read(header) != header.Length || header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            return false;
        }

        int peOffset = BitConverter.ToInt32(header[60..64]);
        if (peOffset < 64 || peOffset > stream.Length - 6)
        {
            return false;
        }

        stream.Position = peOffset;
        Span<byte> pe = stackalloc byte[6];
        return stream.Read(pe) == pe.Length
            && pe[0] == (byte)'P'
            && pe[1] == (byte)'E'
            && pe[2] == 0
            && pe[3] == 0
            && BitConverter.ToUInt16(pe[4..6]) == 0x8664;
    }

    private static bool SameSelectionKeys(DevicePackageCandidate left, DevicePackageCandidate right) =>
        left.TrustTier == right.TrustTier
        && left.Specificity == right.Specificity
        && ParseVersion(left.Manifest?.Version) == ParseVersion(right.Manifest?.Version)
        && string.Equals(left.Manifest?.Id, right.Manifest?.Id, StringComparison.Ordinal);

    private static int TrustRank(DevicePluginTrustTier tier) => tier switch
    {
        DevicePluginTrustTier.WsgmReviewed => 0,
        DevicePluginTrustTier.SignedExternal => 1,
        DevicePluginTrustTier.SideloadedCommunity => 2,
        DevicePluginTrustTier.Developer => 3,
        _ => int.MaxValue,
    };

    private static Version ParseVersion(string? version) =>
        Version.TryParse(version, out Version? parsed) ? parsed : new Version();

    private static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.Exists && (info.LinkTarget is not null
            || (info.Attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static DevicePackageCandidate Reject(
        string path,
        DevicePluginTrustTier tier,
        string code,
        string detail,
        PluginManifest? manifest = null) => new()
        {
            PackagePath = Path.GetFullPath(path),
            TrustTier = tier,
            Manifest = manifest,
            Eligible = false,
            RejectionCode = code,
            Detail = detail,
        };
}

/// <summary>NativeAOT-safe installed-package metadata.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(InstalledDevicePackageRecord))]
public sealed partial class DevicePackageJsonContext : JsonSerializerContext;
