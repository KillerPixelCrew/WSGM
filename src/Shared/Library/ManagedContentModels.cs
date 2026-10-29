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

/// <summary>Whether a shortcut can start, as both surfaces and Steam show it.</summary>
/// <remarks>
///     Exactly three states. <see cref="ManagedContentStorage.Classify" /> is the one place a path observation
///     becomes one of them; a title that has not been checked yet is simply not published.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ContentAvailability>))]
public enum ContentAvailability
{
    /// <summary>Every required file is present.</summary>
    Available,

    /// <summary>
    ///     The storage or library that holds the files is not usable: absent, a different card at its drive,
    ///     unreadable, or a card this device has not accepted.
    /// </summary>
    LibraryMissing,

    /// <summary>The storage is present and readable, but a required file, folder or dependency is missing.</summary>
    FileMissing
}

/// <summary>One path query's result. Access failures are never conflated with absence.</summary>
public enum ContentPathState
{
    /// <summary>The path exists with the expected file or directory kind.</summary>
    Present,

    /// <summary>Not found on the expected, readable volume beneath a readable ancestor.</summary>
    Missing,

    /// <summary>The expected volume, or the drive a literal path names, is not mounted.</summary>
    VolumeAbsent,

    /// <summary>The query failed, met an unsafe path, or found no readable ancestor.</summary>
    Unreadable
}

/// <summary>The source's authoritative backing-content category.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LibrarySourceKind>))]
public enum LibrarySourceKind
{
    /// <summary>An emulated ROM or its primary descriptor.</summary>
    Rom,

    /// <summary>
    ///     A directly started executable: from a configured shortcut folder, or a launcher title's own
    ///     executable on removable storage.
    /// </summary>
    Folder,

    /// <summary>An explicitly authored command.</summary>
    Manual,

    /// <summary>A third-party launcher source. Its titles keep content records only on the managed route.</summary>
    Launcher,

    /// <summary>The selected emulator's own interface, resolved from its active installation.</summary>
    Emulator
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
/// <remarks>
///     Durable intent only. Whether the content is present right now is observed by the availability tracker
///     on every pass and never stored here.
/// </remarks>
public sealed class ManagedContentRecord
{
    /// <summary>The stable identity allocated before Steam shortcut creation.</summary>
    public string Id { get; set; } = "";

    /// <summary>The portable library UUID, empty for legacy and fixed-only records.</summary>
    public string LibraryId { get; set; } = "";

    /// <summary>The configured source identity.</summary>
    public string SourceId { get; set; } = "";

    /// <summary>The title's stable key within its source.</summary>
    public string SourceKey { get; set; } = "";

    /// <summary>The title shown in a launch refusal.</summary>
    public string Name { get; set; } = "";

    /// <summary>The authoritative source kind, such as rom, folder or manual.</summary>
    public LibrarySourceKind SourceKind { get; set; }

    /// <summary>The friendly expected library or storage name, used when nothing better is connected.</summary>
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

    /// <summary>The record's path set: backing content, companions, then the program and working directory.</summary>
    /// <returns>Every reference that names a path, in that order; the unset program fields of a ROM are skipped.</returns>
    public IEnumerable<ManagedContentPath> Paths()
    {
        return new[] { BackingPath }.Concat(RequiredPaths).Concat([Program, WorkingDirectory])
            .Where(path => path.AbsolutePath.Length > 0 || path.VolumeId.Length > 0);
    }
}

/// <summary>The result shared by the launch guard and the shortcut availability tracker.</summary>
/// <param name="Availability">The observed state.</param>
/// <param name="Detail">The actionable explanation.</param>
/// <param name="Path">The validated current backing path, when available.</param>
/// <param name="Content">The detached record with its current system preference resolved.</param>
/// <param name="Launch">The validated emulator launch, when this title uses one.</param>
/// <param name="ProgramPath">The current program path for a direct non-emulator command.</param>
/// <param name="WorkingDirectoryPath">The current working directory for that command.</param>
public sealed record ManagedContentCheck(
    ContentAvailability Availability,
    string Detail,
    string Path = "",
    ManagedContentRecord? Content = null,
    EmulatorResolvedLaunch? Launch = null,
    string ProgramPath = "",
    string WorkingDirectoryPath = "")
{
    /// <summary>Whether all declared content paths were present.</summary>
    public bool Available => Availability == ContentAvailability.Available;
}

/// <summary>One check pass's mount, readability and emulator observations, so each fact is queried once.</summary>
/// <remarks>A pass creates a fresh context; nothing here outlives it, because any of it goes stale on the next mount.</remarks>
public sealed class ManagedContentCheckContext
{
    internal Dictionary<string, string?> Mounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal HashSet<string> ReadableDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal HashSet<string> SafeDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The per-installation prerequisite observations reused within this operation.</summary>
    public EmulatorResolveContext Emulators { get; } = new();

    /// <summary>Supplies a volume's current mount from the owner's own snapshot, or null when it is absent.</summary>
    /// <param name="volumeId">The volume GUID path.</param>
    /// <param name="mount">Its mount path, such as <c>D:\</c>, or null.</param>
    public void UseMount(string volumeId, string? mount)
    {
        Mounts[volumeId] = mount;
    }
}

/// <summary>Reads authoritative importer metadata and resolves current mounts without Steam or WSGM UI.</summary>
public static class ManagedContentStorage
{
    /// <summary>The importer schema shared with its independent native launch consumer.</summary>
    public const int FormatVersion = 2;

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
              && NormalizeLocalRelativePath(before.RelativePath).TrimEnd('\\', '/')
                  .Equals(NormalizeLocalRelativePath(after.RelativePath).TrimEnd('\\', '/'),
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
            RelativePath = NormalizeLocalRelativePath(Path.Combine(root.RelativePath,
                Path.GetRelativePath(root.AbsolutePath, path))),
            Directory = directory
        };
    }

    /// <summary>Returns the authoritative importer state path for the supplied user root.</summary>
    public static string StorePath(string userRoot)
    {
        return Path.Combine(userRoot, "library-import.json");
    }

    /// <summary>Reads only the requested launch record from one importer-state snapshot.</summary>
    /// <remarks>
    ///     A record in a card library launches only while this device's library binding names the same volume
    ///     as the record and needs no repair. A card on a volume the device has not accepted is refused until
    ///     the user chooses Use this card.
    /// </remarks>
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

            if (record.LibraryId.Length > 0)
            {
                var matches = document.RootElement.TryGetProperty("Libraries", out var libraries)
                              && libraries.ValueKind == JsonValueKind.Array
                    ? libraries.EnumerateArray().Where(library =>
                        library.TryGetProperty("Id", out var libraryId)
                        && libraryId.ValueKind == JsonValueKind.String
                        && string.Equals(libraryId.GetString(), record.LibraryId, StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                    : [];
                if (matches.Length != 1
                    || !matches[0].TryGetProperty("VolumeId", out var volumeId)
                    || volumeId.ValueKind != JsonValueKind.String
                    || !string.Equals(volumeId.GetString(), record.BackingPath.VolumeId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "This card is not in use on this device. Choose Use this card under Storage in Game Library.");
                }

                if (matches[0].TryGetProperty("RepairRequired", out var repair) && repair.ValueKind != JsonValueKind.False)
                {
                    throw new InvalidDataException("This card's game list needs repair. Choose Repair library under Storage in Game Library.");
                }

                if (matches[0].TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    record.Location = name.GetString() ?? record.Location;
                }
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
                captured.RelativePath = NormalizeLocalRelativePath(Path.GetRelativePath(new string(mount), full));
            }
        }

        return captured;
    }

    /// <summary>Reads the volume GUID path that a drive root, such as <c>D:\</c>, currently names.</summary>
    /// <param name="root">A mount point with its trailing separator.</param>
    /// <returns>The volume GUID path, or null when nothing is mounted there.</returns>
    public static unsafe string? VolumeAt(string root)
    {
        var volume = stackalloc char[64];
        fixed (char* mount = root)
        {
            return Kernel32.GetVolumeNameForVolumeMountPointW(mount, volume, 64) ? new string(volume) : null;
        }
    }

    /// <summary>Finds the volume's current mount, which may be any letter or folder; null when it is not mounted.</summary>
    /// <remarks>The first, shortest mount wins. A drive letter only addresses the volume, it never identifies it.</remarks>
    public static unsafe string? MountOf(string volumeId, ManagedContentCheckContext? context = null)
    {
        if (context?.Mounts.TryGetValue(volumeId, out var observed) == true)
        {
            return observed;
        }

        char[] mounts;
        fixed (char* volume = volumeId)
        {
            _ = Kernel32.GetVolumePathNamesForVolumeNameW(volume, null, 0, out var required);
            mounts = new char[required];
            fixed (char* paths = mounts)
            {
                _ = Kernel32.GetVolumePathNamesForVolumeNameW(volume, paths, required, out _);
            }
        }

        string? mount = null;
        foreach (var candidate in new string(mounts).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                     .OrderBy(value => value.Length).ThenBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(VolumeAt(candidate), volumeId, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(candidate))
            {
                mount = candidate;
                break;
            }
        }

        if (context is not null)
        {
            context.Mounts[volumeId] = mount;
        }

        return mount;
    }

    /// <summary>Resolves the expected volume to a current usable mount and rejects root escapes.</summary>
    public static string ResolvePath(ManagedContentPath path, ManagedContentCheckContext? context = null)
    {
        if (path.VolumeId.Length == 0)
        {
            return Path.GetFullPath(path.AbsolutePath);
        }

        return ResolveWithin(
            MountOf(path.VolumeId, context) ?? throw new DirectoryNotFoundException("The required storage is not mounted."),
            path.RelativePath);
    }

    /// <summary>Removes dot segments generated by local discovery, retaining strict rejection of rooted or escaping paths.</summary>
    public static string NormalizeLocalRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Split(['\\', '/']).Contains(".."))
        {
            throw new InvalidDataException("A content path escapes its configured storage root.");
        }

        return string.Join('\\', relativePath.Split(['\\', '/'])
            .Where(segment => segment != "."));
    }

    /// <summary>Validates a volume GUID and relative path without following a mount letter.</summary>
    public static string ResolveLibraryPath(string volumeId, string relativePath)
    {
        if (volumeId.Length != 49 || !volumeId.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
            || !volumeId.EndsWith(@"}\", StringComparison.Ordinal)
            || !Guid.TryParseExact(volumeId.Substring(11, 36), "D", out _))
        {
            throw new InvalidDataException("The expected storage volume identity is invalid.");
        }

        if (relativePath.Length > 32700 || Path.IsPathRooted(relativePath)
            || relativePath.IndexOfAny([':', '\0', '<', '>', '"', '|', '?', '*']) >= 0
            || relativePath.Split(['\\', '/']).Any(part => part is ".." || part.EndsWith(' ') || part.EndsWith('.')))
        {
            // The root itself is represented by an empty string, never dot segments.
            throw new InvalidDataException("A library path escapes or aliases its storage root.");
        }

        return relativePath.Length == 0 ? volumeId : volumeId + relativePath.Replace('/', '\\');
    }

    /// <summary>Rejects reparse points in every existing volume-relative path segment.</summary>
    /// <exception cref="FileNotFoundException">A segment does not exist; the segments before it were safe.</exception>
    public static void RejectLibraryReparsePoints(string volumeId, string relativePath,
        ManagedContentCheckContext? context = null)
    {
        _ = ResolveLibraryPath(volumeId, relativePath);
        var current = volumeId;
        var segments = new[] { "" }.Concat(relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in segments)
        {
            current = segment.Length == 0 ? current : Path.Combine(current, segment);
            if (context?.SafeDirectories.Contains(current) == true)
            {
                continue;
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("A library path contains a reparse point. Use a path on this storage.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                context?.SafeDirectories.Add(current);
            }
        }
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

    /// <summary>Queries one path the way every availability decision does.</summary>
    /// <param name="path">
    ///     A volume-bound reference, queried through its volume GUID while that volume is mounted anywhere, or a
    ///     literal absolute path when <see cref="ManagedContentPath.VolumeId" /> is empty.
    /// </param>
    /// <param name="context">The pass's shared observations, or null for a one-off query.</param>
    /// <param name="rejectReparsePoints">Whether every existing segment must be a plain file or folder.</param>
    /// <returns>
    ///     Present; Missing only below a readable ancestor on the expected storage; VolumeAbsent when that storage is
    ///     not mounted; otherwise Unreadable. A literal path's drive says nothing about which card is in it.
    /// </returns>
    public static ContentPathState Probe(ManagedContentPath path, ManagedContentCheckContext? context = null,
        bool rejectReparsePoints = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        context ??= new ManagedContentCheckContext();
        var target = "";
        try
        {
            if (path.VolumeId.Length > 0)
            {
                if (MountOf(path.VolumeId, context) is null)
                {
                    return ContentPathState.VolumeAbsent;
                }

                var relative = NormalizeLocalRelativePath(path.RelativePath);
                target = ResolveLibraryPath(path.VolumeId, relative);
                if (rejectReparsePoints)
                {
                    RejectLibraryReparsePoints(path.VolumeId, relative, context);
                }
            }
            else
            {
                target = Path.GetFullPath(path.AbsolutePath);
                if (Path.GetPathRoot(target) is not { Length: > 0 } root || !Directory.Exists(root))
                {
                    return ContentPathState.VolumeAbsent;
                }
            }

            var attributes = File.GetAttributes(target);
            return ((attributes & FileAttributes.Directory) != 0) == path.Directory
                ? ContentPathState.Present
                : ContentPathState.Missing;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return target.Length > 0 && ReadableAncestor(target, context)
                ? ContentPathState.Missing
                : ContentPathState.Unreadable;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return ContentPathState.Unreadable;
        }
    }

    /// <summary>Establishes that a not-found answer came from storage that can actually be listed.</summary>
    private static bool ReadableAncestor(string target, ManagedContentCheckContext context)
    {
        for (var parent = Path.GetDirectoryName(target); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (context.ReadableDirectories.Contains(parent))
            {
                return true;
            }

            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(parent).GetEnumerator();
                _ = entries.MoveNext();
                context.ReadableDirectories.Add(parent);
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A deleted root or folder: its own parent decides.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>The one mapping from a path observation to the three published states.</summary>
    public static ContentAvailability Classify(ContentPathState state)
    {
        return state switch
        {
            ContentPathState.Present => ContentAvailability.Available,
            ContentPathState.Missing => ContentAvailability.FileMissing,
            _ => ContentAvailability.LibraryMissing
        };
    }

    /// <summary>Checks the backing content, preference and emulator dependencies with one consistent policy.</summary>
    /// <param name="record">The authoritative launch record.</param>
    /// <param name="store">A fresh emulator snapshot acquired by the owner.</param>
    /// <param name="context">An optional operation-local mount and prerequisite cache.</param>
    /// <returns>The availability and resolved launch, or one actionable refusal.</returns>
    /// <remarks>
    ///     Library acceptance is not decided here: the launch helper's <see cref="Read" /> and the host's
    ///     tracker each know the device's library bindings and refuse an unaccepted card before checking files.
    /// </remarks>
    public static ManagedContentCheck Check(ManagedContentRecord record, EmulatorStore store,
        ManagedContentCheckContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(store);
        if (RecordDefect(record) is { } defect)
        {
            return new ManagedContentCheck(ContentAvailability.FileMissing, defect, Content: record);
        }

        context ??= new ManagedContentCheckContext();
        var content = ResolveEmulatorPreference(store, record);
        if (content.SourceKind == LibrarySourceKind.Emulator)
        {
            try
            {
                var launch = EmulatorStorage.ResolveInterface(store, content.EmulatorInstallationId);
                return new ManagedContentCheck(ContentAvailability.Available, "", Content: content, Launch: launch);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                                           or ArgumentException or JsonException)
            {
                return new ManagedContentCheck(ContentAvailability.FileMissing, ex.Message, Content: content);
            }
        }

        var portable = content.LibraryId.Length > 0;
        foreach (var path in new[] { content.BackingPath }.Concat(content.RequiredPaths))
        {
            var state = Probe(path, context, portable);
            if (state != ContentPathState.Present)
            {
                return Unavailable(state, content, "The game files are");
            }
        }

        string backing;
        try
        {
            // GUID paths authorize checks; conventional mount paths preserve emulator and game compatibility.
            backing = ResolvePath(content.BackingPath, context);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return Unavailable(ContentPathState.VolumeAbsent, content, "The game files are");
        }

        var available = new ManagedContentCheck(ContentAvailability.Available, "", backing, content);
        if (content.SourceKind == LibrarySourceKind.Rom)
        {
            try
            {
                var launch = EmulatorStorage.Resolve(store, content.EmulatorInstallationId, content.SystemId,
                    content.CoreId, backing, content.Arguments.Length == 0 ? null : content.Arguments,
                    context.Emulators);
                return available with { Launch = launch };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                           or UnauthorizedAccessException or ArgumentException or JsonException)
            {
                return available with { Availability = ContentAvailability.FileMissing, Detail = ex.Message };
            }
        }

        foreach (var dependency in new[] { content.Program, content.WorkingDirectory })
        {
            var state = Probe(dependency, context, portable && dependency.VolumeId.Equals(content.BackingPath.VolumeId,
                StringComparison.OrdinalIgnoreCase));
            if (state != ContentPathState.Present)
            {
                return Unavailable(state, content, "The program or its working directory is");
            }
        }

        try
        {
            return available with
            {
                ProgramPath = ResolvePath(content.Program, context),
                WorkingDirectoryPath = ResolvePath(content.WorkingDirectory, context)
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return Unavailable(ContentPathState.VolumeAbsent, content, "The program or its working directory is");
        }
    }

    /// <summary>Words one failed probe for a refusal, naming what to connect or what is missing.</summary>
    private static ManagedContentCheck Unavailable(ContentPathState state, ManagedContentRecord content, string what)
    {
        var detail = state switch
        {
            ContentPathState.Missing => $"{what} missing from {content.Location}. Rescan its source, or remove the title in Game Library.",
            ContentPathState.VolumeAbsent => $"Connect {content.Location} and choose Recheck.",
            _ => $"{content.Location} could not be read. Check the storage, then choose Recheck."
        };
        return new ManagedContentCheck(Classify(state), detail, Content: content);
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
                       ? NormalizeLocalRelativePath(left.RelativePath).Equals(
                           NormalizeLocalRelativePath(right.RelativePath), StringComparison.OrdinalIgnoreCase)
                       : left.AbsolutePath.Equals(right.AbsolutePath, StringComparison.OrdinalIgnoreCase));
        }

        return before.Id == after.Id && before.SourceId == after.SourceId && before.SourceKey == after.SourceKey
               && before.LibraryId == after.LibraryId && before.SourceKind == after.SourceKind
               && EmulatorStorage.NormalizeSystemId(before.SystemId) ==
               EmulatorStorage.NormalizeSystemId(after.SystemId)
               && before.EmulatorInstallationId == after.EmulatorInstallationId && before.CoreId == after.CoreId
               && before.FollowSystemPreference == after.FollowSystemPreference
               && before.RawArguments == after.RawArguments
               && before.Arguments.SequenceEqual(after.Arguments) && SamePath(before.BackingPath, after.BackingPath)
               && SamePath(before.Program, after.Program) && SamePath(before.WorkingDirectory, after.WorkingDirectory)
               && SamePath(before.SourceRoot, after.SourceRoot) &&
               before.RequiredPaths.Count == after.RequiredPaths.Count
               && before.RequiredPaths.Zip(after.RequiredPaths).All(pair => SamePath(pair.First, pair.Second));
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
                   LibraryId: not null,
                   Location: not null, SystemId: not null, EmulatorInstallationId: not null, CoreId: not null,
                   RawArguments: not null, Arguments: not null, RequiredPaths: not null
               }
               && (record.LibraryId.Length == 0 || Guid.TryParseExact(record.LibraryId, "D", out var libraryId)
                   && libraryId != Guid.Empty && record.BackingPath is { VolumeId.Length: > 0 })
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
