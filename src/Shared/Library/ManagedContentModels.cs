// Shared between WSGM and WSGM.PackagedLaunch (linked as a source file).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WSGM.Interop;

namespace WSGM.Core;

/// <summary>The observed state of a managed title's actual content.</summary>
public enum ManagedContentAvailability
{
    /// <summary>Not validated in this session.</summary>
    Unknown,

    /// <summary>All required content is present.</summary>
    Available,

    /// <summary>The expected backing volume is not mounted.</summary>
    StorageUnavailable,

    /// <summary>The volume is present but required content is missing.</summary>
    ContentMissing,

    /// <summary>The selected emulator, core or its prerequisites are unavailable.</summary>
    EmulatorUnavailable,

    /// <summary>Content could not be inspected.</summary>
    Unreadable
}

/// <summary>The source's authoritative backing-content category.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LibrarySourceKind>))]
public enum LibrarySourceKind
{
    /// <summary>An emulated ROM or its primary descriptor.</summary>
    Rom,

    /// <summary>A configured executable or shortcut folder.</summary>
    Folder,

    /// <summary>An explicitly authored command.</summary>
    Manual,

    /// <summary>A third-party launcher's installed-game metadata.</summary>
    Launcher
}

/// <summary>The launch strategy is independent of retained availability metadata.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ManagedLaunchKind>))]
public enum ManagedLaunchKind
{
    /// <summary>A contained direct child.</summary>
    Direct,

    /// <summary>The shortcut retains its original native launch route.</summary>
    Native
}

/// <summary>A path bound to a volume rather than its current drive letter.</summary>
public sealed class ManagedContentPath
{
    /// <summary>The original full path, retained for fixed paths and diagnostics.</summary>
    public string AbsolutePath { get; set; } = "";

    /// <summary>The expected Windows volume GUID path, independent of its mount point.</summary>
    public string VolumeId { get; set; } = "";

    /// <summary>The path inside the expected volume.</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>Whether this reference requires a directory rather than a file.</summary>
    public bool Directory { get; set; }

    /// <summary>Returns a detached mutable path reference.</summary>
    public ManagedContentPath Copy()
    {
        return (ManagedContentPath)MemberwiseClone();
    }
}

/// <summary>The launch and storage identity retained independently of Steam's command.</summary>
public sealed class ManagedContentRecord
{
    /// <summary>The stable identity allocated before Steam shortcut creation.</summary>
    public string Id { get; set; } = "";

    /// <summary>The configured source identity.</summary>
    public string SourceId { get; set; } = "";

    /// <summary>The title's stable key within its source.</summary>
    public string SourceKey { get; set; } = "";

    /// <summary>The title shown in a launch refusal.</summary>
    public string Name { get; set; } = "";

    /// <summary>The authoritative source kind, such as rom, folder or manual.</summary>
    public LibrarySourceKind SourceKind { get; set; }

    /// <summary>The friendly expected library or storage name.</summary>
    public string Location { get; set; } = "";

    /// <summary>The emulated system, independent of emulator choice.</summary>
    public string SystemId { get; set; } = "";

    /// <summary>The selected installation identity, independent of its active program version.</summary>
    public string EmulatorInstallationId { get; set; } = "";

    /// <summary>The explicitly selected RetroArch core, when required.</summary>
    public string CoreId { get; set; } = "";

    /// <summary>Whether launch resolves the system preference instead of pinning one emulator installation.</summary>
    public bool FollowSystemPreference { get; set; }

    /// <summary>The ROM, executable or descriptor that this shortcut requires.</summary>
    public ManagedContentPath BackingPath { get; set; } = new();

    /// <summary>The configured source root retained for mount-independent rescanning.</summary>
    public ManagedContentPath SourceRoot { get; set; } = new() { Directory = true };

    /// <summary>Companion tracks or other required backing files.</summary>
    public List<ManagedContentPath> RequiredPaths { get; set; } = [];

    /// <summary>The original program for a non-emulator command.</summary>
    public ManagedContentPath Program { get; set; } = new();

    /// <summary>The original command's working directory.</summary>
    public ManagedContentPath WorkingDirectory { get; set; } = new() { Directory = true };

    /// <summary>Declared argument tokens for a structured non-emulator command.</summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>The verbatim authored command arguments, with optional explicit content token.</summary>
    public string RawArguments { get; set; } = "";

    /// <summary>The latest persisted availability observation.</summary>
    public ManagedContentAvailability Availability { get; set; }

    /// <summary>The actionable reason attached to that observation.</summary>
    public string AvailabilityDetail { get; set; } = "";

    /// <summary>Whether a launcher handoff uses the existing followed-game strategy.</summary>
    public ManagedLaunchKind LaunchKind { get; set; } = ManagedLaunchKind.Direct;

    /// <summary>Copies the record and its mutable path and argument collections.</summary>
    public ManagedContentRecord Copy()
    {
        var copy = (ManagedContentRecord)MemberwiseClone();
        copy.BackingPath = BackingPath.Copy();
        copy.SourceRoot = SourceRoot.Copy();
        copy.Program = Program.Copy();
        copy.WorkingDirectory = WorkingDirectory.Copy();
        copy.RequiredPaths = [.. RequiredPaths.Select(path => path.Copy())];
        copy.Arguments = [.. Arguments];
        return copy;
    }
}

/// <summary>A managed record's confirmed Steam identity and detached metadata.</summary>
/// <param name="AppId">The shortcut identity, or zero while creation is unconfirmed.</param>
/// <param name="Content">The authoritative launch and content record.</param>
public sealed record ManagedContentEntry(uint AppId, ManagedContentRecord Content);

/// <summary>The result shared by the launch guard and runtime availability monitor.</summary>
/// <param name="Availability">The observed state.</param>
/// <param name="Detail">The actionable explanation.</param>
/// <param name="Path">The validated current backing path, when available.</param>
/// <param name="Content">The detached record with its current system preference resolved.</param>
/// <param name="Launch">The validated emulator launch, when this title uses one.</param>
/// <param name="ProgramPath">The current program path for a direct non-emulator command.</param>
/// <param name="WorkingDirectoryPath">The current working directory for that command.</param>
public sealed record ManagedContentCheck(
    ManagedContentAvailability Availability,
    string Detail,
    string Path = "",
    ManagedContentRecord? Content = null,
    EmulatorResolvedLaunch? Launch = null,
    string ProgramPath = "",
    string WorkingDirectoryPath = "")
{
    /// <summary>Whether all declared content paths were present.</summary>
    public bool Available => Availability == ManagedContentAvailability.Available;
}

/// <summary>One reconciliation operation's mount and emulator-validation observations.</summary>
public sealed class ManagedContentCheckContext
{
    internal Dictionary<string, string?> Mounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The per-installation prerequisite observations reused within this operation.</summary>
    public EmulatorResolveContext Emulators { get; } = new();
}

/// <summary>Reads authoritative importer metadata and resolves current mounts without Steam or WSGM UI.</summary>
public static class ManagedContentStorage
{
    /// <summary>The importer schema shared with its independent native launch consumer.</summary>
    public const int FormatVersion = 1;

    /// <summary>Creates the durable identity before Steam assigns an app id.</summary>
    public static string ContentId(string sourceId, string key)
    {
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(sourceId.ToUpperInvariant() + "\u001f" + key.ToUpperInvariant())), 0, 16)
            .ToLowerInvariant();
    }

    /// <summary>Captures the common authoritative identity and backing location for every managed source.</summary>
    /// <param name="sourceId">The stable configured source identity.</param>
    /// <param name="key">The stable content key inside that source.</param>
    /// <param name="name">The title shown in a refusal.</param>
    /// <param name="kind">The authoritative source category.</param>
    /// <param name="location">The user-visible backing library label.</param>
    /// <param name="backingPath">The content whose presence the title requires.</param>
    /// <param name="directory">Whether that content must be a directory.</param>
    /// <param name="binding">An already captured source-relative reference, when available.</param>
    /// <returns>A detached record ready for its source-specific launch fields.</returns>
    public static ManagedContentRecord CreateRecord(string sourceId, string key, string name, LibrarySourceKind kind,
        string location, string backingPath, bool directory = false, ManagedContentPath? binding = null)
    {
        return new ManagedContentRecord
        {
            Id = ContentId(sourceId, key),
            SourceId = sourceId,
            SourceKey = key,
            Name = name,
            SourceKind = kind,
            Location = location,
            BackingPath = binding ?? CapturePath(backingPath, directory)
        };
    }

    /// <summary>Compares configured locations by expected volume identity, or by their fixed absolute path.</summary>
    public static bool SameLocation(ManagedContentPath before, ManagedContentPath after)
    {
        return before.VolumeId.Length > 0 && after.VolumeId.Length > 0
            ? before.VolumeId.Equals(after.VolumeId, StringComparison.OrdinalIgnoreCase)
              && before.RelativePath.TrimEnd('\\', '/').Equals(after.RelativePath.TrimEnd('\\', '/'),
                  StringComparison.OrdinalIgnoreCase)
            : before.AbsolutePath.TrimEnd('\\', '/')
                .Equals(after.AbsolutePath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Binds a discovered descendant using its already-captured source root.</summary>
    public static ManagedContentPath BindPath(ManagedContentPath root, string path, bool directory = false)
    {
        if (root.VolumeId.Length == 0 || !StoragePaths.IsUnder(root.AbsolutePath, path))
        {
            return CapturePath(path, directory);
        }

        return new ManagedContentPath
        {
            AbsolutePath = Path.GetFullPath(path), VolumeId = root.VolumeId,
            RelativePath = Path.Combine(root.RelativePath, Path.GetRelativePath(root.AbsolutePath, path)),
            Directory = directory
        };
    }

    /// <summary>Returns the authoritative importer state path for the supplied user root.</summary>
    public static string StorePath(string userRoot)
    {
        return Path.Combine(userRoot, "library-import.json");
    }

    /// <summary>Reads only the requested launch record from one importer-state snapshot.</summary>
    public static ManagedContentRecord Read(string userRoot, string id)
    {
        using var stream = new FileStream(StorePath(userRoot), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.TryGetProperty("Version", out var version)
            && (!version.TryGetInt32(out var number) || number > FormatVersion))
        {
            throw new InvalidDataException("These imported title records require a newer WSGM launcher.");
        }

        if (!document.RootElement.TryGetProperty("Entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("WSGM's imported title records could not be read.");
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("Content", out var content) || content.ValueKind != JsonValueKind.Object
                                                                  || !content.TryGetProperty("Id", out var storedId) ||
                                                                  storedId.ValueKind != JsonValueKind.String
                                                                  || storedId.GetString() != id)
            {
                continue;
            }

            var record = content.Deserialize(ManagedContentJsonContext.Default.ManagedContentRecord)
                         ?? throw new InvalidDataException(
                             "This title's launch record is incomplete. Rescan its source in Game Library.");
            if (RecordDefect(record) is { } defect)
            {
                throw new InvalidDataException(defect);
            }

            return record;
        }

        throw new InvalidDataException(
            "This title's WSGM launch record is missing. Rescan its source in Game Library.");
    }

    /// <summary>Resolves a preference from one already-loaded receipt snapshot for batch reconciliation.</summary>
    public static ManagedContentRecord ResolveEmulatorPreference(EmulatorStore store, ManagedContentRecord content)
    {
        if (!content.FollowSystemPreference)
        {
            return content;
        }

        var copy = content.Copy();
        var preferred = PreferredSystem(store, copy.SystemId);
        copy.EmulatorInstallationId = preferred?.InstallationId ?? "";
        copy.CoreId = preferred?.CoreId ?? "";
        return copy;
    }

    /// <summary>Reads one canonical system preference without materializing or copying launch records.</summary>
    public static EmulatorSystemPreference? PreferredSystem(EmulatorStore store, string systemId)
    {
        return store.SystemPreferences.FirstOrDefault(preference =>
            EmulatorStorage.NormalizeSystemId(preference.SystemId)
            == EmulatorStorage.NormalizeSystemId(systemId));
    }

    /// <summary>Captures a full path and its current volume identity when Windows exposes one.</summary>
    public static unsafe ManagedContentPath CapturePath(string path, bool directory = false)
    {
        var full = Path.GetFullPath(path);
        ManagedContentPath captured = new() { AbsolutePath = full, Directory = directory };
        if (full.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return captured;
        }

        var capacity = full.Length + 2;
        var mount = stackalloc char[capacity];
        var volume = stackalloc char[64];
        fixed (char* fileName = full)
        {
            if (Kernel32.GetVolumePathNameW(fileName, mount, (uint)capacity)
                && Kernel32.GetVolumeNameForVolumeMountPointW(mount, volume, 64))
            {
                captured.VolumeId = new string(volume);
                captured.RelativePath = Path.GetRelativePath(new string(mount), full);
            }
        }

        return captured;
    }

    /// <summary>Resolves the expected volume to a current usable mount and rejects root escapes.</summary>
    public static unsafe string ResolvePath(ManagedContentPath path, ManagedContentCheckContext? context = null)
    {
        if (path.VolumeId.Length == 0)
        {
            return Path.GetFullPath(path.AbsolutePath);
        }

        if (context?.Mounts.TryGetValue(path.VolumeId, out var observed) == true)
        {
            return ResolveWithin(
                observed ?? throw new DirectoryNotFoundException("The required storage is not mounted."),
                path.RelativePath);
        }

        char[] mounts;
        fixed (char* volume = path.VolumeId)
        {
            _ = Kernel32.GetVolumePathNamesForVolumeNameW(volume, null, 0, out var required);
            mounts = new char[required];
            fixed (char* paths = mounts)
            {
                _ = Kernel32.GetVolumePathNamesForVolumeNameW(volume, paths, required, out _);
            }
        }

        string? mount = null;
        var actual = stackalloc char[64];
        foreach (var candidate in new string(mounts).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                     .OrderBy(value => value.Length).ThenBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            fixed (char* mountPath = candidate)
            {
                if (Kernel32.GetVolumeNameForVolumeMountPointW(mountPath, actual, 64)
                    && string.Equals(new string(actual), path.VolumeId, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(candidate))
                {
                    mount = candidate;
                    break;
                }
            }
        }

        if (mount is null)
        {
            if (context is not null)
            {
                context.Mounts[path.VolumeId] = null;
            }

            throw new DirectoryNotFoundException("The required storage is not mounted.");
        }

        if (context is not null)
        {
            context.Mounts[path.VolumeId] = mount;
        }

        return ResolveWithin(mount, path.RelativePath);
    }

    private static string ResolveWithin(string mount, string relativePath)
    {
        var root = Path.GetFullPath(mount).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!StoragePaths.IsUnder(root, resolved))
        {
            throw new InvalidDataException("A content path escapes its configured storage root.");
        }

        return resolved;
    }

    /// <summary>Checks actual backing content and companion files, preserving missing-storage and unreadable distinctions.</summary>
    private static ManagedContentCheck Validate(ManagedContentRecord record, ManagedContentCheckContext? context)
    {
        try
        {
            var primary = ResolvePath(record.BackingPath, context);
            foreach (var path in new[] { record.BackingPath }.Concat(record.RequiredPaths))
            {
                var resolved = ReferenceEquals(path, record.BackingPath) ? primary : ResolvePath(path, context);
                try
                {
                    var attributes = File.GetAttributes(resolved);
                    if (path.Directory != attributes.HasFlag(FileAttributes.Directory))
                    {
                        return Missing(record);
                    }
                }
                catch (FileNotFoundException)
                {
                    return Missing(record);
                }
                catch (DirectoryNotFoundException)
                {
                    return Missing(record);
                }
            }

            return new ManagedContentCheck(ManagedContentAvailability.Available, "Available", primary);
        }
        catch (DirectoryNotFoundException)
        {
            return new ManagedContentCheck(ManagedContentAvailability.StorageUnavailable,
                $"Connect {record.Location} and choose Recheck.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException)
        {
            return new ManagedContentCheck(ManagedContentAvailability.Unreadable,
                $"The content on {record.Location} could not be checked. Recheck it in Game Library.");
        }
    }

    /// <summary>Checks the backing content, preference and emulator dependencies with one consistent policy.</summary>
    /// <param name="record">The authoritative launch record.</param>
    /// <param name="store">A fresh emulator snapshot acquired by the owner.</param>
    /// <param name="context">An optional operation-local mount and prerequisite cache.</param>
    /// <returns>The availability and resolved launch, or one actionable refusal.</returns>
    public static ManagedContentCheck Check(ManagedContentRecord record, EmulatorStore store,
        ManagedContentCheckContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(store);
        if (RecordDefect(record) is { } defect)
        {
            return new ManagedContentCheck(ManagedContentAvailability.Unreadable, defect, Content: record);
        }

        var content = ResolveEmulatorPreference(store, record);
        var checkedContent = Validate(content, context) with { Content = content };
        if (!checkedContent.Available)
        {
            return checkedContent;
        }

        if (content.SourceKind == LibrarySourceKind.Rom)
        {
            try
            {
                var launch = EmulatorStorage.Resolve(store, content.EmulatorInstallationId, content.SystemId,
                    content.CoreId, checkedContent.Path, content.Arguments.Length == 0 ? null : content.Arguments,
                    context?.Emulators);
                return checkedContent with { Launch = launch };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                           or UnauthorizedAccessException or ArgumentException or JsonException)
            {
                return checkedContent with
                {
                    Availability = ManagedContentAvailability.EmulatorUnavailable,
                    Detail = ex.Message
                };
            }
        }

        // Native launcher routes may retain storage metadata without an indirection or a program recipe.
        if (content.LaunchKind == ManagedLaunchKind.Native)
        {
            return checkedContent;
        }

        try
        {
            var program = ResolvePath(content.Program, context);
            var directory = ResolvePath(content.WorkingDirectory, context);
            if (!File.Exists(program) || !Directory.Exists(directory))
            {
                return checkedContent with
                {
                    Availability = ManagedContentAvailability.EmulatorUnavailable,
                    Detail = "The program or its working directory is missing. Repair it or edit this shortcut."
                };
            }

            return checkedContent with { ProgramPath = program, WorkingDirectoryPath = directory };
        }
        catch (DirectoryNotFoundException)
        {
            return checkedContent with
            {
                Availability = ManagedContentAvailability.StorageUnavailable,
                Detail = $"Connect {content.Location} and choose Recheck."
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException)
        {
            return checkedContent with { Availability = ManagedContentAvailability.Unreadable, Detail = ex.Message };
        }
    }

    /// <summary>Compares durable launch and content intent, excluding observations and preview diagnostics.</summary>
    public static bool SameLaunch(ManagedContentRecord? before, ManagedContentRecord? after)
    {
        if (before is null || after is null)
        {
            return before is null && after is null;
        }

        if (RecordDefect(before) is not null || RecordDefect(after) is not null)
        {
            return false;
        }

        static bool SamePath(ManagedContentPath left, ManagedContentPath right)
        {
            return left.Directory == right.Directory
                   && left.VolumeId.Equals(right.VolumeId, StringComparison.OrdinalIgnoreCase)
                   && (left.VolumeId.Length > 0
                       ? left.RelativePath.Equals(right.RelativePath, StringComparison.OrdinalIgnoreCase)
                       : left.AbsolutePath.Equals(right.AbsolutePath, StringComparison.OrdinalIgnoreCase));
        }

        return before.Id == after.Id && before.SourceId == after.SourceId && before.SourceKey == after.SourceKey
               && before.Name == after.Name && before.SourceKind == after.SourceKind &&
               before.Location == after.Location
               && EmulatorStorage.NormalizeSystemId(before.SystemId) ==
               EmulatorStorage.NormalizeSystemId(after.SystemId)
               && before.EmulatorInstallationId == after.EmulatorInstallationId && before.CoreId == after.CoreId
               && before.FollowSystemPreference == after.FollowSystemPreference && before.LaunchKind == after.LaunchKind
               && before.RawArguments == after.RawArguments
               && before.Arguments.SequenceEqual(after.Arguments) && SamePath(before.BackingPath, after.BackingPath)
               && SamePath(before.Program, after.Program) && SamePath(before.WorkingDirectory, after.WorkingDirectory)
               && SamePath(before.SourceRoot, after.SourceRoot) &&
               before.RequiredPaths.Count == after.RequiredPaths.Count
               && before.RequiredPaths.Zip(after.RequiredPaths).All(pair => SamePath(pair.First, pair.Second));
    }

    private static ManagedContentCheck Missing(ManagedContentRecord record)
    {
        return new ManagedContentCheck(ManagedContentAvailability.ContentMissing,
            $"The content is missing from {record.Location}. Rescan this title's source in Game Library.");
    }

    /// <summary>Checks the record's required shape before copying or resolving authored fields.</summary>
    /// <param name="record">The loaded metadata.</param>
    /// <returns>An actionable missing-field error, or null for a usable shape.</returns>
    public static string? RecordDefect(ManagedContentRecord record)
    {
        static bool PathValid(ManagedContentPath? path)
        {
            return path is
                { AbsolutePath: not null, VolumeId: not null, RelativePath: not null };
        }

        return record is
               {
                   Id.Length: > 0, SourceId: not null, SourceKey: not null, Name: not null,
                   Location: not null, SystemId: not null, EmulatorInstallationId: not null, CoreId: not null,
                   RawArguments: not null, Arguments: not null, RequiredPaths: not null,
                   AvailabilityDetail: not null
               }
               && record.Arguments.All(argument => argument is not null)
               && record.RequiredPaths.All(PathValid) && PathValid(record.BackingPath)
               && PathValid(record.Program) && PathValid(record.WorkingDirectory) && PathValid(record.SourceRoot)
            ? null
            : "This title's launch record is incomplete. Rescan its source in Game Library.";
    }
}

/// <summary>Source-generated serialization shared by the app and dependency-light launch helper.</summary>
[JsonSerializable(typeof(ManagedContentRecord))]
public sealed partial class ManagedContentJsonContext : JsonSerializerContext;
