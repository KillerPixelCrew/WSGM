using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WSGM.Interop;

namespace WSGM.DeviceLab.Packaging;

/// <summary>
///     Pins one package tree and every accepted file so validation and packing consume identical bytes.
/// </summary>
internal sealed class DeviceLabPackageSnapshot : IDisposable
{
    private readonly HashSet<string> _canonicalPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceLabPackageFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICollection<PluginPackageValidationIssue> _issues;
    private readonly NativePackageSource _source;
    private bool _disposed;

    private DeviceLabPackageSnapshot(
        NativePackageSource source,
        ICollection<PluginPackageValidationIssue> issues)
    {
        _source = source;
        _issues = issues;
    }

    /// <summary>Accepted regular package files keyed by canonical relative path.</summary>
    internal IReadOnlyList<DeviceLabPackageFile> Files =>
        [.. _files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal)];

    /// <summary>Structural issues observed while pinning the package tree.</summary>
    internal IReadOnlyList<PluginPackageValidationIssue> Issues => [.. _issues];

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var file in _files.Values)
        {
            file.Dispose();
        }

        _files.Clear();
        _canonicalPaths.Clear();
        _source.Dispose();
    }

    /// <summary>Captures a bounded no-follow view of an existing package directory.</summary>
    internal static DeviceLabPackageSnapshot Capture(
        string root,
        ICollection<PluginPackageValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var source = NativePackageSource.TryOpen(root)
                     ?? throw new DirectoryNotFoundException(
                         $"Package source directory does not exist: '{root}'.");
        DeviceLabPackageSnapshot snapshot = new(source, issues);
        try
        {
            Stack<string> pending = new();
            pending.Push(source.RootPath);
            var entryCount = 0;
            var fileCount = 0;
            long totalBytes = 0;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                var entries = TakeBoundedEntries(
                    Directory.EnumerateFileSystemEntries(directory),
                    PluginPackageWorkflow.MaximumPackageEntries - entryCount,
                    cancellationToken,
                    out var exceeded);
                if (exceeded)
                {
                    var relative = Path.GetRelativePath(source.RootPath, directory).Replace('\\', '/');
                    issues.Add(new PluginPackageValidationIssue(
                        "package-too-many-entries",
                        relative is "." ? string.Empty : relative,
                        $"Package contains more than {PluginPackageWorkflow.MaximumPackageEntries} filesystem entries."));
                    return snapshot;
                }

                foreach (var path in entries.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entryCount++;
                    var relative = Path.GetRelativePath(source.RootPath, path).Replace('\\', '/');
                    using var entry = source.OpenEntry(path);
                    if (entry.IsReparsePoint)
                    {
                        issues.Add(new PluginPackageValidationIssue(
                            "reparse-path",
                            relative,
                            "Package paths may not contain links or reparse points."));
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        source.RetainDirectory(entry);
                        pending.Push(path);
                        continue;
                    }

                    var violation = PluginPackageWorkflow.PackageBudgetViolation(
                        fileCount,
                        totalBytes,
                        entry.Length);
                    if (violation is not null)
                    {
                        issues.Add(new PluginPackageValidationIssue(
                            violation,
                            relative,
                            PluginPackageWorkflow.PackageBudgetMessage(violation)));
                        return snapshot;
                    }

                    DeviceLabPackageFile file = new(relative, entry.TakeHandle(), entry.Length);
                    if (!snapshot._canonicalPaths.Add(relative)
                        || !snapshot._files.TryAdd(relative, file))
                    {
                        file.Dispose();
                        issues.Add(new PluginPackageValidationIssue(
                            "duplicate-path",
                            relative,
                            "Package contains duplicate canonical file paths."));
                        continue;
                    }

                    fileCount++;
                    totalBytes += entry.Length;
                }
            }

            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    /// <summary>Looks up one captured file without reopening its source path.</summary>
    internal bool TryGetFile(string relativePath, out DeviceLabPackageFile file)
    {
        return _files.TryGetValue(relativePath.Replace('\\', '/'), out file!);
    }

    /// <summary>
    ///     Takes at most the remaining entry budget plus one overflow observation before sorting.
    /// </summary>
    internal static IReadOnlyList<string> TakeBoundedEntries(
        IEnumerable<string> entries,
        int remaining,
        CancellationToken cancellationToken,
        out bool exceeded)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(remaining);
        List<string> accepted = [];
        using var enumerator = entries.GetEnumerator();
        while (accepted.Count < remaining)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                exceeded = false;
                return accepted;
            }

            accepted.Add(enumerator.Current);
        }

        cancellationToken.ThrowIfCancellationRequested();
        exceeded = enumerator.MoveNext();
        return accepted;
    }
}

/// <summary>One regular package file retained with write and delete sharing denied.</summary>
internal sealed class DeviceLabPackageFile : IDisposable
{
    private readonly FileStream _stream;

    internal DeviceLabPackageFile(string relativePath, SafeFileHandle handle, long length)
    {
        RelativePath = relativePath;
        Length = length;
        _stream = new FileStream(handle, FileAccess.Read, 64 * 1024, false);
    }

    /// <summary>Canonical package-relative path.</summary>
    internal string RelativePath { get; }

    /// <summary>Stable file length observed from the retained handle.</summary>
    internal long Length { get; }

    /// <summary>Retained seekable stream. Call <see cref="Rewind" /> before each read.</summary>
    internal Stream Stream => _stream;

    /// <inheritdoc />
    public void Dispose()
    {
        _stream.Dispose();
    }

    /// <summary>Rewinds the retained stream to the start.</summary>
    internal void Rewind()
    {
        _stream.Position = 0;
    }

    /// <summary>Reads stable owned bytes without reopening the path.</summary>
    internal bool TryReadAllBytes(int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (Length < 0 || Length > maximumBytes)
        {
            return false;
        }

        Rewind();
        var owned = new byte[(int)Length];
        _stream.ReadExactly(owned);
        bytes = owned;
        return true;
    }
}
