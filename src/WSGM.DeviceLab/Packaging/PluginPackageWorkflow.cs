using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Packaging;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Packaging;

/// <summary>One package-validation failure with a stable code and path.</summary>
internal sealed record PluginPackageValidationIssue(string Code, string Path, string Message);

/// <summary>Offline package validation result.</summary>
internal sealed record PluginPackageValidationReport
{
    /// <summary>Whether every deterministic offline check passed.</summary>
    public required bool Valid { get; init; }

    /// <summary>Parsed package identity when available.</summary>
    public string? PackageId { get; init; }

    /// <summary>Parsed package version when available.</summary>
    public string? PackageVersion { get; init; }

    /// <summary>Validation failures in deterministic order.</summary>
    public IReadOnlyList<PluginPackageValidationIssue> Issues { get; init; } = [];
}

/// <summary>Deterministic validation and packing for developer plugin packages.</summary>
internal static class PluginPackageWorkflow
{
    /// <summary>Canonical package manifest path.</summary>
    public const string ManifestPath = "plugin.wsgm.json";

    internal const int MaximumPackageEntries = 1024;
    internal const int MaximumPackageFiles = 512;
    internal const long MaximumPackageFileBytes = 128L * 1024 * 1024;
    internal const long MaximumPackageBytes = 512L * 1024 * 1024;

    private static readonly DateTimeOffset DeterministicTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Validates one source directory without loading its assembly or touching hardware.</summary>
    /// <param name="sourceDirectory">Package source directory.</param>
    /// <param name="cancellationToken">Cancels bounded source capture and validation.</param>
    /// <returns>Deterministic validation report.</returns>
    public static PluginPackageValidationReport ValidateOffline(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        var openingFailure = CaptureSource(
            sourceDirectory,
            cancellationToken,
            out var snapshot);
        if (openingFailure is not null)
        {
            return openingFailure;
        }

        using var captured = snapshot
                             ?? throw new InvalidOperationException("Package capture returned no snapshot.");
        return ValidateOffline(captured, out _, cancellationToken);
    }

    private static PluginPackageValidationReport ValidateOffline(
        DeviceLabPackageSnapshot snapshot,
        out IReadOnlyList<DeviceLabPackageFile> packageFiles,
        CancellationToken cancellationToken)
    {
        packageFiles = [];
        cancellationToken.ThrowIfCancellationRequested();
        List<PluginPackageValidationIssue> issues = [.. snapshot.Issues];
        if (!snapshot.TryGetFile(ManifestPath, out var manifestFile))
        {
            issues.Add(Issue("missing-manifest", ManifestPath, "Package manifest is absent."));
            return Report(null, null, [.. issues]);
        }

        var manifestRead = ReadManifestBounded(manifestFile);
        if (!manifestRead.IsValid || manifestRead.Manifest is null)
        {
            issues.AddRange(manifestRead.Errors.Select(error =>
                Issue(StableCode(error.Code), error.Path, error.Message)));
            return Report(null, null, [.. issues]);
        }

        var manifest = manifestRead.Manifest;
        if (manifest.ApiVersion != DeviceApi.Version)
        {
            issues.Add(Issue("runtime-api", "apiVersion", "Package does not use this exact SDK API version."));
        }

        List<DeviceLabPackageFile> files = [];
        foreach (var file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = file.RelativePath;
            if (!CaptureBundleLayout.IsSafeRelativePath(relative))
            {
                issues.Add(Issue("unsafe-path", relative, "Package file path is not canonical and relative."));
                continue;
            }

            files.Add(file);
        }

        IReadOnlyList<string> relativeFiles = [.. files.Select(file => file.RelativePath)];
        if (CheckRequiredFile(manifest.EntryAssembly, relativeFiles, issues)
            && snapshot.TryGetFile(manifest.EntryAssembly, out var entryAssembly))
        {
            if (!IsX64Pe(entryAssembly))
            {
                issues.Add(Issue(
                    "architecture-unsupported",
                    manifest.EntryAssembly,
                    "Plugin entry assembly is not a readable x64 PE image."));
            }
        }

        ValidateForbiddenProvisioningArtifacts(relativeFiles, issues);
        ValidateGlyphProfiles(snapshot, relativeFiles, issues, cancellationToken);
        packageFiles = files;

        return Report(
            manifest.Id,
            manifest.Version,
            [
                .. issues.OrderBy(issue => issue.Path, StringComparer.Ordinal)
                    .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            ]);
    }

    /// <summary>Writes a deterministic package after a clean offline validation.</summary>
    /// <param name="sourceDirectory">Validated source directory.</param>
    /// <param name="outputPath">New explicit <c>.wsgmpkg</c> path.</param>
    /// <param name="boundaries">Filesystem safety boundaries.</param>
    /// <param name="cancellationToken">Cancels validation or atomic archive publication.</param>
    /// <returns>The offline validation report for the packed source.</returns>
    public static PluginPackageValidationReport Pack(
        string sourceDirectory,
        string outputPath,
        DeviceLabPathBoundaries boundaries,
        CancellationToken cancellationToken = default)
    {
        return Pack(
            sourceDirectory,
            outputPath,
            boundaries,
            null,
            cancellationToken);
    }

    internal static PluginPackageValidationReport Pack(
        string sourceDirectory,
        string outputPath,
        DeviceLabPathBoundaries boundaries,
        Action? sourceValidated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        var openingFailure = CaptureSource(
            sourceDirectory,
            cancellationToken,
            out var snapshot);
        if (openingFailure is not null)
        {
            return openingFailure;
        }

        using var captured = snapshot
                             ?? throw new InvalidOperationException("Package capture returned no snapshot.");
        var report = ValidateOffline(
            captured,
            out var packageFiles,
            cancellationToken);
        if (!report.Valid)
        {
            return report;
        }

        var decision = DeviceLabOutputPathPolicy.Evaluate(
            outputPath,
            DeviceLabOutputTargetKind.NewFile,
            boundaries);
        if (!decision.IsAllowed || decision.FullPath is null)
        {
            return report with
            {
                Valid = false,
                Issues = [Issue("invalid-output", outputPath, decision.Reason ?? "Output path rejected.")]
            };
        }

        var temporary = $"{decision.FullPath}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(decision.FullPath)!);
        cancellationToken.ThrowIfCancellationRequested();
        var recheck = DeviceLabOutputPathPolicy.Evaluate(
            decision.FullPath,
            DeviceLabOutputTargetKind.NewFile,
            boundaries);
        if (!recheck.IsAllowed)
        {
            return report with
            {
                Valid = false,
                Issues = [Issue("invalid-output", outputPath, recheck.Reason ?? "Output path changed before write.")]
            };
        }

        try
        {
            sourceValidated?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            using (FileStream stream = new(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                var fileCount = 0;
                long totalBytes = 0;
                foreach (var file in packageFiles.OrderBy(
                             file => file.RelativePath,
                             StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var written = WriteEntry(
                        archive,
                        file,
                        fileCount,
                        totalBytes,
                        cancellationToken);
                    fileCount++;
                    totalBytes += written;
                }
            }

            using (FileStream flushed = new(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                _ = flushed.Length;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, decision.FullPath);
            return report;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Formats enum-backed issue codes as the CLI's stable lower-kebab vocabulary.</summary>
    /// <param name="value">Typed SDK or importer error.</param>
    /// <returns>A lower-kebab issue code.</returns>
    internal static string StableCode(Enum value)
    {
        var name = value.ToString();
        StringBuilder result = new(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (index > 0 && char.IsUpper(character))
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }

    private static PluginPackageValidationReport? CaptureSource(
        string sourceDirectory,
        CancellationToken cancellationToken,
        out DeviceLabPackageSnapshot? snapshot)
    {
        snapshot = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Report(null, null, [Issue("invalid-root", "", exception.GetType().Name)]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<PluginPackageValidationIssue> issues = [];
        try
        {
            snapshot = DeviceLabPackageSnapshot.Capture(root, issues, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            return Report(null, null, [Issue("missing-root", "", "Package directory does not exist.")]);
        }
        catch (UnauthorizedAccessException)
        {
            return Report(null, null, [Issue("unreadable-root", "", "Package directory access was denied.")]);
        }
        catch (InvalidDataException exception)
        {
            return Report(null, null, [Issue("invalid-root", "", exception.Message)]);
        }
        catch (IOException exception)
        {
            return Report(null, null, [Issue("unreadable-root", "", exception.GetType().Name)]);
        }

        return null;
    }

    private static void ValidateForbiddenProvisioningArtifacts(
        IEnumerable<string> paths,
        List<PluginPackageValidationIssue> issues)
    {
        string[] forbiddenExtensions = [".sys", ".inf", ".cat", ".ps1", ".cmd", ".bat", ".reg"];
        foreach (var path in paths.Where(path => forbiddenExtensions.Contains(
                     Path.GetExtension(path),
                     StringComparer.OrdinalIgnoreCase)))
        {
            issues.Add(Issue(
                "forbidden-provisioning-artifact",
                path,
                "Developer packages cannot provision drivers, services, tasks, registry repair, or helper installation."));
        }
    }

    private static void ValidateGlyphProfiles(
        DeviceLabPackageSnapshot snapshot,
        IReadOnlyList<string> packageFiles,
        List<PluginPackageValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var imported = GlyphPackageImporter.Import(
            new SnapshotGlyphPackageSource(snapshot, cancellationToken));

        foreach (var error in imported.Errors)
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
        foreach (var profile in imported.Profiles)
        {
            expected.Add(GlyphPackageLayout.ProfileManifest(profile.Manifest.ProfileId));
            foreach (var asset in profile.Manifest.Assets)
            {
                expected.Add(GlyphPackageLayout.Asset(asset.Sha256, asset.Format));
            }

            expected.Add(profile.Manifest.NoticePath);
        }

        foreach (var path in packageFiles.Where(path => path.StartsWith("glyphs/", StringComparison.Ordinal))
                     .Except(expected, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            issues.Add(Issue(
                "glyph-unreferenced-file",
                path,
                "Glyph package file is not reachable from a directly enumerated profile."));
        }
    }

    internal static string? PackageBudgetViolation(
        int acceptedFileCount,
        long acceptedBytes,
        long nextFileBytes)
    {
        if (acceptedFileCount >= MaximumPackageFiles)
        {
            return "package-too-many-files";
        }

        if (nextFileBytes is < 0 or > MaximumPackageFileBytes)
        {
            return "file-too-large";
        }

        if (acceptedBytes < 0
            || acceptedBytes > MaximumPackageBytes
            || nextFileBytes > MaximumPackageBytes - acceptedBytes)
        {
            return "package-too-large";
        }

        return null;
    }

    internal static bool PackageEntryBudgetExceeded(int acceptedEntryCount)
    {
        return acceptedEntryCount >= MaximumPackageEntries;
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
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static bool CheckRequiredFile(
        string relative,
        IReadOnlyList<string> packageFiles,
        List<PluginPackageValidationIssue> issues)
    {
        var canonical = relative.Replace('\\', '/');
        if (packageFiles.Contains(canonical, StringComparer.Ordinal))
        {
            return true;
        }

        issues.Add(Issue("missing-file", canonical, "Manifest-referenced package file is absent."));
        return false;
    }

    private static long WriteEntry(
        ZipArchive archive,
        DeviceLabPackageFile file,
        int acceptedFileCount,
        long acceptedBytes,
        CancellationToken cancellationToken)
    {
        var violation = PackageBudgetViolation(acceptedFileCount, acceptedBytes, file.Length);
        if (violation is not null)
        {
            throw new InvalidDataException(PackageBudgetMessage(violation));
        }

        var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.NoCompression);
        entry.LastWriteTime = DeterministicTimestamp;
        entry.ExternalAttributes = 0;
        using var output = entry.Open();
        var buffer = new byte[64 * 1024];
        long written = 0;
        file.Rewind();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = file.Stream.Read(buffer);
            if (read == 0)
            {
                break;
            }

            if (read > MaximumPackageFileBytes - written
                || read > MaximumPackageBytes - acceptedBytes - written)
            {
                throw new InvalidDataException("Package source exceeded its validated size while packing.");
            }

            output.Write(buffer, 0, read);
            written += read;
        }

        return written;
    }

    internal static PluginManifestReadResult ReadManifestBounded(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > ManifestLimits.MaxDocumentBytes)
        {
            return new PluginManifestReadResult(
                null,
                [
                    new ManifestValidationError(
                        "",
                        ManifestValidationCode.DocumentTooLarge,
                        $"Manifest is {stream.Length} bytes, above the "
                        + $"{ManifestLimits.MaxDocumentBytes}-byte limit.")
                ]);
        }

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return PluginManifestReader.Read(bytes);
    }

    private static PluginManifestReadResult ReadManifestBounded(DeviceLabPackageFile file)
    {
        if (!file.TryReadAllBytes(ManifestLimits.MaxDocumentBytes, out var bytes))
        {
            return new PluginManifestReadResult(
                null,
                [
                    new ManifestValidationError(
                        "",
                        ManifestValidationCode.DocumentTooLarge,
                        $"Manifest is outside the {ManifestLimits.MaxDocumentBytes}-byte limit.")
                ]);
        }

        return PluginManifestReader.Read(bytes);
    }

    private static bool IsX64Pe(DeviceLabPackageFile file)
    {
        try
        {
            file.Rewind();
            using PEReader pe = new(file.Stream, PEStreamOptions.LeaveOpen);
            if (pe.PEHeaders.CoffHeader.Machine is not Machine.Amd64
                || pe.PEHeaders.CorHeader is null
                || !pe.HasMetadata)
            {
                return false;
            }

            var metadata = pe.GetMetadataReader();
            return metadata.IsAssembly;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or BadImageFormatException)
        {
            return false;
        }
    }

    internal static string PackageBudgetMessage(string violation)
    {
        return violation switch
        {
            "package-too-many-files" => $"Package contains more than {MaximumPackageFiles} files.",
            "file-too-large" => $"A package file exceeds {MaximumPackageFileBytes} bytes.",
            "package-too-large" => $"Package exceeds {MaximumPackageBytes} total bytes.",
            _ => "Package exceeds a filesystem budget."
        };
    }

    private static PluginPackageValidationIssue Issue(string code, string path, string message)
    {
        return new PluginPackageValidationIssue(code, path, message);
    }

    private static PluginPackageValidationReport Report(
        string? id,
        string? version,
        IReadOnlyList<PluginPackageValidationIssue> issues)
    {
        return new PluginPackageValidationReport
        {
            Valid = issues.Count == 0,
            PackageId = id,
            PackageVersion = version,
            Issues = issues
        };
    }

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

/// <summary>Serves glyph validation from the same pinned file handles used by package validation.</summary>
internal sealed class SnapshotGlyphPackageSource(
    DeviceLabPackageSnapshot snapshot,
    CancellationToken cancellationToken = default) : IGlyphPackageSource
{
    private readonly DeviceLabPackageSnapshot _snapshot = snapshot
                                                          ?? throw new ArgumentNullException(nameof(snapshot));

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateProfileIds()
    {
        cancellationToken.ThrowIfCancellationRequested();
        return
        [
            .. _snapshot.Files
                .Select(file => file.RelativePath)
                .Where(path => path.StartsWith("glyphs/profiles/", StringComparison.Ordinal)
                               && path.EndsWith(".json", StringComparison.Ordinal)
                               && path.AsSpan("glyphs/profiles/".Length).IndexOf('/') < 0)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <inheritdoc />
    public bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _snapshot.TryGetFile(relativePath, out var file)
                   && file.TryReadAllBytes(maximumBytes, out bytes);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            bytes = [];
            return false;
        }
    }
}
