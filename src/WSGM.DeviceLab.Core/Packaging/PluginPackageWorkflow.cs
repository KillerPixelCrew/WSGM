using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSGM.Device.Contracts.Glyphs;
using WSGM.Device.Contracts.Ipc;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Evidence;
using WSGM.DeviceLab.Core.Preflight;
using WSGM.DeviceLab.Core.Scaffolding;

namespace WSGM.DeviceLab.Core.Packaging;

/// <summary>One package-validation failure with a stable code and path.</summary>
public sealed record PluginPackageValidationIssue(string Code, string Path, string Message);

/// <summary>Offline package validation result; it deliberately grants no runtime authority.</summary>
public sealed record PluginPackageValidationReport
{
    /// <summary>Whether every deterministic offline check passed.</summary>
    public required bool Valid { get; init; }

    /// <summary>Parsed package identity when available.</summary>
    public string? PackageId { get; init; }

    /// <summary>Parsed package version when available.</summary>
    public string? PackageVersion { get; init; }

    /// <summary>Declared publisher; not verified merely from manifest text.</summary>
    public string? DeclaredPublisher { get; init; }

    /// <summary>Validation failures in deterministic order.</summary>
    public IReadOnlyList<PluginPackageValidationIssue> Issues { get; init; } = [];

    /// <summary>Exact canonical file hashes used by packing and review.</summary>
    public IReadOnlyDictionary<string, string> FileHashes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Offline validation never grants package trust.</summary>
    public bool GrantsTrust => false;

    /// <summary>Offline validation never grants privilege.</summary>
    public bool GrantsPrivilege => false;

    /// <summary>Offline validation never claims hardware verification.</summary>
    public bool GrantsHardwareVerification => false;

    /// <summary>Offline validation never claims retail support.</summary>
    public bool GrantsRetailSupport => false;
}

/// <summary>Deterministic validation and packing for developer plugin packages.</summary>
public static class PluginPackageWorkflow
{
    /// <summary>Canonical package manifest path.</summary>
    public const string ManifestPath = "plugin.wsgm.json";

    /// <summary>Canonical package hash manifest.</summary>
    public const string HashesPath = "hashes.sha256";

    private static readonly DateTimeOffset DeterministicTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Validates one source directory without loading its assembly or touching hardware.</summary>
    /// <param name="sourceDirectory">Package source directory.</param>
    /// <returns>Deterministic validation report.</returns>
    public static PluginPackageValidationReport ValidateOffline(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        List<PluginPackageValidationIssue> issues = [];
        string root;
        try
        {
            root = Path.GetFullPath(sourceDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Report(null, null, null, [Issue("invalid-root", "", exception.GetType().Name)], EmptyHashes());
        }

        if (!Directory.Exists(root))
        {
            return Report(null, null, null, [Issue("missing-root", "", "Package directory does not exist.")], EmptyHashes());
        }
        if (IsLink(root))
        {
            return Report(null, null, null, [Issue("reparse-path", "", "Package root may not be a link or reparse point.")], EmptyHashes());
        }

        string manifestPath = Path.Combine(root, ManifestPath);
        if (!File.Exists(manifestPath))
        {
            return Report(null, null, null, [Issue("missing-manifest", ManifestPath, "Package manifest is absent.")], EmptyHashes());
        }

        PluginManifestReadResult manifestRead = PluginManifestReader.Read(File.ReadAllBytes(manifestPath));
        if (!manifestRead.IsValid || manifestRead.Manifest is null)
        {
            return Report(
                null,
                null,
                null,
                [.. manifestRead.Errors.Select(error => Issue(error.Code.ToString(), error.Path, error.Message))],
                EmptyHashes());
        }

        PluginManifest manifest = manifestRead.Manifest;
        if (manifest.MinApiVersion > DeviceProtocol.MaxSupportedVersion
            || manifest.MaxApiVersion < DeviceProtocol.MinSupportedVersion)
        {
            issues.Add(Issue("runtime-api", "minApiVersion", "Package does not overlap this runtime API window."));
        }

        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        foreach (string path in EnumeratePackageFiles(root, issues))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!CaptureBundleLayout.IsSafeRelativePath(relative)
                || string.Equals(relative, HashesPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(relative, HashesPath, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Issue("unsafe-path", relative, "Package file path is not canonical and relative."));
                }

                continue;
            }

            FileInfo file = new(path);
            if (file.Length > CaptureSchema.MaximumBlobBytes)
            {
                issues.Add(Issue("file-too-large", relative, "Package file exceeds its size budget."));
                continue;
            }

            hashes[relative] = HashFile(path);
        }

        CheckRequiredFile(manifest.EntryPoint, root, hashes, issues);
        if (manifest.Provenance.LicenseNoticePath is { Length: > 0 } notice)
        {
            CheckRequiredFile(notice, root, hashes, issues);
        }

        ValidateEvidenceLock(root, manifest, hashes, issues);
        ValidateGeneratedBoundary(root, hashes, issues);
        ValidateUnreviewedPrivilegeRequests(manifest, hashes.Keys, issues);
        ValidateGlyphProfiles(root, manifest, hashes, issues);

        return Report(
            manifest.Id,
            manifest.Version,
            manifest.Publisher,
            [.. issues.OrderBy(issue => issue.Path, StringComparer.Ordinal).ThenBy(issue => issue.Code, StringComparer.Ordinal)],
            hashes);
    }

    /// <summary>Writes a deterministic package after a clean offline validation.</summary>
    /// <param name="sourceDirectory">Validated source directory.</param>
    /// <param name="outputPath">New explicit <c>.wsgmpkg</c> path.</param>
    /// <param name="boundaries">Filesystem safety boundaries.</param>
    /// <returns>The validation report used to authorize only archive creation.</returns>
    public static PluginPackageValidationReport Pack(
        string sourceDirectory,
        string outputPath,
        DeviceLabPathBoundaries boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        PluginPackageValidationReport report = ValidateOffline(sourceDirectory);
        if (!report.Valid)
        {
            return report;
        }

        DeviceLabOutputPathDecision decision = DeviceLabOutputPathPolicy.Evaluate(
            outputPath,
            DeviceLabOutputTargetKind.NewFile,
            boundaries);
        if (!decision.IsAllowed || decision.FullPath is null)
        {
            return report with
            {
                Valid = false,
                Issues = [Issue("invalid-output", outputPath, decision.Reason ?? "Output path rejected.")],
            };
        }

        string root = Path.GetFullPath(sourceDirectory);
        string temporary = $"{decision.FullPath}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(decision.FullPath)!);
        DeviceLabOutputPathDecision recheck = DeviceLabOutputPathPolicy.Evaluate(
            decision.FullPath,
            DeviceLabOutputTargetKind.NewFile,
            boundaries);
        if (!recheck.IsAllowed)
        {
            return report with
            {
                Valid = false,
                Issues = [Issue("invalid-output", outputPath, recheck.Reason ?? "Output path changed before write.")],
            };
        }

        try
        {
            using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                foreach ((string relative, string _) in report.FileHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    WriteEntry(archive, relative, File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))));
                }

                CaptureHashEntry[] entries = [.. report.FileHashes.Select(pair =>
                    new CaptureHashEntry(pair.Key, pair.Value))];
                WriteEntry(archive, HashesPath, Encoding.UTF8.GetBytes(CaptureHashFile.Serialize(entries)));
            }

            using (FileStream flushed = new(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                _ = flushed.Length;
            }

            File.Move(temporary, decision.FullPath);
            return report;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void ValidateEvidenceLock(
        string root,
        PluginManifest manifest,
        IReadOnlyDictionary<string, string> hashes,
        ICollection<PluginPackageValidationIssue> issues)
    {
        const string EvidencePath = "evidence.lock.json";
        if (!hashes.ContainsKey(EvidencePath))
        {
            issues.Add(Issue("missing-evidence-lock", EvidencePath, "Package must retain its evidence lock."));
            return;
        }

        try
        {
            EvidenceLock? evidence = JsonSerializer.Deserialize(
                File.ReadAllBytes(Path.Combine(root, EvidencePath)),
                DeviceLabJsonContext.Default.EvidenceLock);
            if (evidence is null
                || evidence.SchemaVersion != EvidenceLockBuilder.CurrentSchemaVersion
                || manifest.Devices.All(device => !string.Equals(device.Id, evidence.DeviceId, StringComparison.Ordinal))
                || evidence.Modules.Any(module => !manifest.Devices.SelectMany(device => device.Modules)
                    .Any(reference => string.Equals(reference.Id, module.ModuleId, StringComparison.Ordinal)
                        && reference.Version == module.Version)))
            {
                issues.Add(Issue("invalid-evidence-lock", EvidencePath, "Evidence lock does not match a device and every pinned module."));
            }
        }
        catch (JsonException)
        {
            issues.Add(Issue("invalid-evidence-lock", EvidencePath, "Evidence lock JSON is malformed."));
        }
    }

    private static void ValidateGeneratedBoundary(
        string root,
        IReadOnlyDictionary<string, string> hashes,
        ICollection<PluginPackageValidationIssue> issues)
    {
        string outputPath = Path.Combine(root, "scaffold-output.json");
        if (!File.Exists(outputPath))
        {
            return;
        }

        try
        {
            ScaffoldOutputManifest? output = JsonSerializer.Deserialize(
                File.ReadAllBytes(outputPath),
                DeviceLabJsonContext.Default.ScaffoldOutputManifest);
            if (output is null || ScaffoldSchemaValidator.Validate(output).Count != 0)
            {
                issues.Add(Issue("invalid-scaffold-record", "scaffold-output.json", "Scaffold ownership record is invalid."));
                return;
            }

            foreach (ScaffoldOutputFile generated in output.Files.Where(file =>
                file.Ownership is ScaffoldFileOwnership.Generated))
            {
                if (!hashes.TryGetValue(generated.Path, out string? current)
                    || !string.Equals(current, generated.Sha256, StringComparison.Ordinal))
                {
                    issues.Add(Issue("generated-boundary", generated.Path, "Generator-owned file differs from its reviewed scaffold record."));
                }
            }
        }
        catch (JsonException)
        {
            issues.Add(Issue("invalid-scaffold-record", "scaffold-output.json", "Scaffold record JSON is malformed."));
        }
    }

    private static void ValidateUnreviewedPrivilegeRequests(
        PluginManifest manifest,
        IEnumerable<string> paths,
        ICollection<PluginPackageValidationIssue> issues)
    {
        foreach (DependencyDeclaration dependency in manifest.Dependencies.Where(dependency =>
            dependency.InstallOwner is DependencyInstallOwner.WsgmInstaller))
        {
            issues.Add(Issue(
                "unreviewed-installer-dependency",
                $"dependencies/{dependency.Id}",
                "Developer packages cannot extend WSGM-provisioned dependencies."));
        }

        string[] forbiddenExtensions = [".sys", ".inf", ".cat", ".ps1", ".cmd", ".bat", ".reg"];
        foreach (string path in paths.Where(path => forbiddenExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase)))
        {
            issues.Add(Issue(
                "unreviewed-privileged-artifact",
                path,
                "Developer packages cannot provision drivers, services, tasks, registry repair, or helper installation."));
        }
    }

    private static void ValidateGlyphProfiles(
        string root,
        PluginManifest manifest,
        IReadOnlyDictionary<string, string> hashes,
        ICollection<PluginPackageValidationIssue> issues)
    {
        GlyphPackageImportResult imported;
        try
        {
            imported = GlyphPackageImporter.Import(
                manifest,
                new ImmutableGlyphPackageDirectorySource(root));
        }
        catch (InvalidDataException exception)
        {
            issues.Add(Issue("glyph-package-root", "glyphs", exception.Message));
            return;
        }

        foreach (GlyphPackageImportError error in imported.Errors)
        {
            issues.Add(Issue(
                $"glyph-{ToKebabCase(error.Code.ToString())}",
                error.Path,
                $"{error.ProfileId}: {error.Message}"));
        }

        if (!imported.IsValid)
        {
            return;
        }

        HashSet<string> expected = new(StringComparer.Ordinal);
        foreach (GlyphProfilePackageReference reference in manifest.GlyphProfiles)
        {
            expected.Add(GlyphPackageLayout.ProfileManifest(reference.ManifestSha256));
        }
        foreach (ImportedGlyphProfile profile in imported.Profiles)
        {
            foreach (GlyphAssetLockEntry asset in profile.Manifest.Assets)
            {
                expected.Add(GlyphPackageLayout.Asset(asset.Sha256, asset.Format));
                expected.Add(GlyphPackageLayout.GeneratedAsset(asset.Sha256, asset.Format));
                expected.Add(GlyphPackageLayout.Notice(asset.Provenance.LicenseNoticeSha256));
            }
            expected.Add(GlyphPackageLayout.Notice(profile.Manifest.Provenance.LicenseNoticeSha256));
        }

        foreach (string path in hashes.Keys.Where(path => path.StartsWith("glyphs/", StringComparison.Ordinal))
            .Except(expected, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal))
        {
            issues.Add(Issue(
                "glyph-unreferenced-file",
                path,
                "Glyph package output is not reachable from the canonical profile lock."));
        }
    }

    internal static IReadOnlyList<string> EnumeratePackageFiles(
        string root,
        ICollection<PluginPackageValidationIssue> issues)
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
                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (IsLink(entry))
                {
                    issues.Add(Issue(
                        "reparse-path",
                        relative,
                        "Package paths may not contain links or reparse points."));
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.Exists && (info.LinkTarget is not null
            || (info.Attributes & FileAttributes.ReparsePoint) != 0);
    }

    private static string ToKebabCase(string value)
    {
        StringBuilder builder = new(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('-');
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static void CheckRequiredFile(
        string relative,
        string root,
        IReadOnlyDictionary<string, string> hashes,
        ICollection<PluginPackageValidationIssue> issues)
    {
        string canonical = relative.Replace('\\', '/');
        string resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !hashes.ContainsKey(canonical))
        {
            issues.Add(Issue("missing-file", canonical, "Manifest-referenced package file is absent."));
        }
    }

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = DeterministicTimestamp;
        entry.ExternalAttributes = 0;
        using Stream output = entry.Open();
        output.Write(bytes);
    }

    private static PluginPackageValidationIssue Issue(string code, string path, string message) =>
        new(code, path, message);

    private static IReadOnlyDictionary<string, string> EmptyHashes() =>
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static PluginPackageValidationReport Report(
        string? id,
        string? version,
        string? publisher,
        IReadOnlyList<PluginPackageValidationIssue> issues,
        IReadOnlyDictionary<string, string> hashes) => new()
    {
        Valid = issues.Count == 0,
        PackageId = id,
        PackageVersion = version,
        DeclaredPublisher = publisher,
        Issues = issues,
        FileHashes = hashes,
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Preserve the original packing failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original packing failure.
        }
    }
}
