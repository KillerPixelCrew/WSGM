using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static WSGM.Interop.Kernel32;

namespace WSGM.Interop;

/// <summary>Locks every source path component against replacement while package files are copied.</summary>
internal sealed class NativePackageSource : IDisposable
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private readonly List<SafeFileHandle> _directoryHandles = [];
    private bool _disposed;

    private NativePackageSource(string rootPath, NativePathIdentity rootIdentity)
    {
        RootPath = rootPath;
        RootIdentity = rootIdentity;
    }

    /// <summary>Canonical lexical source root whose ancestors are held against rename or deletion.</summary>
    internal string RootPath { get; }

    /// <summary>Filesystem identity observed from the secured source-root handle.</summary>
    internal NativePathIdentity RootIdentity { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var index = _directoryHandles.Count - 1; index >= 0; index--)
        {
            _directoryHandles[index].Dispose();
        }

        _directoryHandles.Clear();
    }

    /// <summary>Secures an existing directory tree root, or returns null when the path is absent.</summary>
    internal static NativePackageSource? TryOpen(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Stack<string> ancestors = [];
        DirectoryInfo? current = new(root);
        while (current is not null)
        {
            ancestors.Push(current.FullName);
            current = current.Parent;
        }

        List<SafeFileHandle> handles = [];
        try
        {
            while (ancestors.Count > 0)
            {
                var ancestor = ancestors.Pop();
                var entry = TryOpenEntry(ancestor);
                if (entry is null)
                {
                    DisposeHandles(handles);
                    return null;
                }

                if (entry.IsReparsePoint)
                {
                    entry.Dispose();
                    throw new InvalidDataException(
                        "Package source may not traverse a link or reparse point.");
                }

                if (!entry.IsDirectory)
                {
                    entry.Dispose();
                    DisposeHandles(handles);
                    return null;
                }

                var rootIdentity = entry.Identity;
                handles.Add(entry.TakeHandle());
                if (ancestors.Count != 0)
                {
                    continue;
                }

                NativePackageSource source = new(root, rootIdentity);
                source._directoryHandles.AddRange(handles);
                handles.Clear();
                return source;
            }

            return null;
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    /// <summary>Opens one enumerated entry without following a reparse point.</summary>
    internal NativePackageSourceEntry OpenEntry(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = TryOpenEntry(path);
        return entry ?? throw new IOException(
            $"Package source entry disappeared before it could be secured: '{path}'.");
    }

    /// <summary>Keeps an opened directory name stable through the remainder of traversal.</summary>
    internal void RetainDirectory(NativePackageSourceEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsDirectory || entry.IsReparsePoint)
        {
            throw new InvalidDataException(
                "Only ordinary package source directories may be retained for traversal.");
        }

        _directoryHandles.Add(entry.TakeHandle());
    }

    private static NativePackageSourceEntry? TryOpenEntry(string path)
    {
        var probe = OpenPath(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (probe.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            probe.Dispose();
            return error is ErrorFileNotFound or ErrorPathNotFound
                ? null
                : throw NativeIoException("open", path, error);
        }

        try
        {
            var probeInformation = ReadInformation(probe, path);
            var isDirectory = (probeInformation.Attributes & FileAttributeDirectory) != 0;
            var isReparsePoint = (probeInformation.Attributes & FileAttributeReparsePoint) != 0;
            if (isDirectory || isReparsePoint)
            {
                NativePackageSourceEntry result = new(
                    probe,
                    probeInformation.Identity,
                    isDirectory,
                    isReparsePoint,
                    0);
                probe = null;
                return result;
            }

            var readHandle = OpenPath(
                path,
                GenericRead,
                FileShareRead,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagSequentialScan);
            try
            {
                if (readHandle.IsInvalid)
                {
                    throw NativeIoException("open for reading", path, Marshal.GetLastPInvokeError());
                }

                var readInformation = ReadInformation(readHandle, path);
                if (readInformation.Identity != probeInformation.Identity
                    || (readInformation.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                {
                    throw new InvalidDataException(
                        $"Package source entry changed while it was being secured: '{path}'.");
                }

                NativePackageSourceEntry result = new(
                    readHandle,
                    readInformation.Identity,
                    false,
                    false,
                    readInformation.Length);
                readHandle = null;
                return result;
            }
            finally
            {
                readHandle?.Dispose();
            }
        }
        finally
        {
            probe?.Dispose();
        }
    }

    private static void DisposeHandles(List<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }

        handles.Clear();
    }

    private static NativeEntryInformation ReadInformation(SafeFileHandle handle, string path)
    {
        if (!NativePathIdentityReader.TryRead(
                handle,
                out var information,
                out var error))
        {
            throw NativeIoException("inspect", path, error);
        }

        return new NativeEntryInformation(
            information.Attributes,
            information.Identity,
            information.Length);
    }

    private static IOException NativeIoException(string operation, string path, int error)
    {
        return new IOException(
            $"Could not {operation} package source path '{path}'.",
            new Win32Exception(error));
    }

    private static SafeFileHandle OpenPath(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint flags)
    {
        var handle = CreateFileHandleW(
            path,
            desiredAccess,
            shareMode,
            0,
            OpenExisting,
            flags,
            0);
        return new SafeFileHandle(handle, true);
    }

    private readonly record struct NativeEntryInformation(
        uint Attributes,
        NativePathIdentity Identity,
        long Length);
}

/// <summary>One no-follow package source entry held against replacement.</summary>
internal sealed class NativePackageSourceEntry : IDisposable
{
    private SafeFileHandle? _handle;

    internal NativePackageSourceEntry(
        SafeFileHandle handle,
        NativePathIdentity identity,
        bool isDirectory,
        bool isReparsePoint,
        long length)
    {
        _handle = handle;
        Identity = identity;
        IsDirectory = isDirectory;
        IsReparsePoint = isReparsePoint;
        Length = length;
    }

    /// <summary>Filesystem identity observed from the no-follow handle.</summary>
    internal NativePathIdentity Identity { get; }

    /// <summary>Whether the secured entry is a directory.</summary>
    internal bool IsDirectory { get; }

    /// <summary>Whether the secured entry itself is a reparse point.</summary>
    internal bool IsReparsePoint { get; }

    /// <summary>File length observed after the read handle blocked writers and replacement.</summary>
    internal long Length { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }

    /// <summary>Transfers the secured file handle into a read-only stream.</summary>
    internal FileStream OpenReadStream()
    {
        if (IsDirectory || IsReparsePoint)
        {
            throw new InvalidOperationException("Only ordinary package files can be read.");
        }

        return new FileStream(TakeHandle(), FileAccess.Read, 64 * 1024, false);
    }

    /// <summary>Transfers ownership of the underlying no-follow handle.</summary>
    internal SafeFileHandle TakeHandle()
    {
        var handle = _handle
                     ?? throw new ObjectDisposedException(nameof(NativePackageSourceEntry));
        _handle = null;
        return handle;
    }
}
