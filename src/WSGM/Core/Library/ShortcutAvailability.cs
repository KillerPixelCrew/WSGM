using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.Core;

/// <summary>One mounted local volume as one availability pass identified it.</summary>
/// <param name="VolumeId">The volume GUID path, or empty for a drive Windows gives no volume, such as a network drive.</param>
/// <param name="Mount">Where it is mounted now, such as <c>D:\</c>. Only an address, never an identity.</param>
/// <param name="Name">
///     What to call it: a card's own Steam library marker label or volume label, a fixed disk's label, or its
///     drive when it has none.
/// </param>
/// <param name="CardId">
///     The card's own identity, read through its volume: the WSGM manifest UUID, else Steam's marker content id.
///     Empty for fixed disks and unmarked media.
/// </param>
/// <param name="Removable">Whether the volume is removable or hot-pluggable storage.</param>
public sealed record ObservedVolume(string VolumeId, string Mount, string Name, string CardId, bool Removable)
{
    /// <summary>Whether this volume is the card a binding names, by volume or by the card's own identity.</summary>
    public bool Holds(ShortcutBinding binding)
    {
        return VolumeId.Length > 0 && string.Equals(VolumeId, binding.VolumeId, StringComparison.OrdinalIgnoreCase)
               || CardId.Length > 0 && string.Equals(CardId, binding.CardId, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The card a Steam shortcut's literal drive path was last found on.</summary>
/// <remarks>
///     A literal path such as <c>D:\Games\Foo.exe</c> only means whatever is in D: right now; a handheld's reader
///     gives every card the same letter. The binding remembers which card held the file the last time it was
///     present, so a different card in that drive reads as the wrong card rather than as deleted files.
/// </remarks>
/// <param name="AccountId">The Steam account whose shortcut list it belongs to.</param>
/// <param name="AppId">The shortcut.</param>
/// <param name="Path">The literal path, exactly as resolved from the shortcut.</param>
/// <param name="VolumeId">The volume GUID path the file was found on.</param>
/// <param name="CardId">That card's own identity when it has one, so a new volume GUID for the same card still matches.</param>
/// <param name="Name">The card's name at the time, shown while it is out.</param>
public sealed record ShortcutBinding(uint AccountId, uint AppId, string Path, string VolumeId, string CardId,
    string Name);

/// <summary>Whether one non-Steam shortcut can start, and where its files live.</summary>
/// <param name="AppId">Steam's shortcut id.</param>
/// <param name="Name">The name Steam shows.</param>
/// <param name="Owned">Whether WSGM created it; the user's own shortcuts are observed the same way.</param>
/// <param name="ContentId">The managed content id its Target names, or empty.</param>
/// <param name="LibraryId">The card library that content belongs to, or empty.</param>
/// <param name="State">One of the three published states.</param>
/// <param name="Location">The card, disk or library holding the files, by name.</param>
/// <param name="Detail">What to do when it is unavailable, naming the card or the missing file.</param>
public sealed record ShortcutStatus(
    uint AppId,
    string Name,
    bool Owned,
    string ContentId,
    string LibraryId,
    ContentAvailability State,
    string Location,
    string Detail)
{
    /// <summary>Whether every required file is present.</summary>
    public bool Available => State == ContentAvailability.Available;

    /// <summary>The state as both surfaces word it.</summary>
    public string Label => ShortcutStatusPresentation.Label(State);
}

/// <summary>The one wording of the three availability states, shared by both surfaces and Steam.</summary>
public static class ShortcutStatusPresentation
{
    /// <summary>The short label for a state.</summary>
    public static string Label(ContentAvailability state)
    {
        return state switch
        {
            ContentAvailability.Available => "Available",
            ContentAvailability.LibraryMissing => "Unavailable · library missing",
            _ => "Unavailable · files missing"
        };
    }
}

/// <summary>What one Steam shortcut needs on disk.</summary>
/// <param name="Shortcut">The shortcut as Steam lists it.</param>
/// <param name="ContentId">The managed record its WSGM helper Target names, or empty.</param>
/// <param name="LiteralPaths">
///     Absolute paths taken as written: a follow route's install folder, or the Target and Start In of any other
///     shortcut. Each means whatever volume holds that drive now.
/// </param>
public sealed record ShortcutRequirement(
    ExistingShortcut Shortcut,
    string ContentId,
    IReadOnlyList<ManagedContentPath> LiteralPaths)
{
    /// <summary>Whether a managed content record, bound to its volume, decides this shortcut.</summary>
    public bool Managed => ContentId.Length > 0;
}

/// <summary>The literal-path verdict for one path of one shortcut.</summary>
/// <param name="State">The published state.</param>
/// <param name="Location">The card or disk to name.</param>
/// <param name="Detail">What to do about it.</param>
/// <param name="Binding">The binding to keep for this path, or null when it has none yet.</param>
public sealed record LiteralVerdict(ContentAvailability State, string Location, string Detail, ShortcutBinding? Binding);

/// <summary>Works out what each Steam shortcut needs, and what its literal paths' observations mean.</summary>
/// <remarks>Pure: no file, volume or Steam access, so every decision here is unit-testable.</remarks>
public static class ShortcutRequirements
{
    /// <summary>Derives the paths a shortcut needs from its Target, Start In and, for the WSGM helper, its options.</summary>
    /// <param name="shortcut">The shortcut as Steam lists it.</param>
    /// <returns>
    ///     The requirement, or null for a shortcut with nothing local to check: a packaged title, a protocol or
    ///     <c>steam://</c> link, a network path or a relative Target.
    /// </returns>
    public static ShortcutRequirement? Resolve(ExistingShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        if (PackagedLaunchCommand.TryParseManagedTarget(shortcut.Target, out var launcher, out var id)
            && IsHelper(launcher))
        {
            return new ShortcutRequirement(shortcut, id, []);
        }

        var target = CommandShortcut.Unquote(shortcut.Target);
        if (IsHelper(target))
        {
            return PackagedLaunchCommand.TryParseFollow(shortcut.LaunchOptions.Trim(), out var follow, out _)
                   && LocalAbsolute(follow.Directory)
                ? new ShortcutRequirement(shortcut, "", [Literal(follow.Directory, true)])
                : null;
        }

        List<ManagedContentPath> paths = [];
        if (LocalAbsolute(target))
        {
            paths.Add(Literal(target, false));
        }

        var start = CommandShortcut.Unquote(shortcut.StartDirectory);
        if (LocalAbsolute(start))
        {
            paths.Add(Literal(start, true));
        }

        return paths.Count == 0 ? null : new ShortcutRequirement(shortcut, "", paths);
    }

    /// <summary>Decides one literal path from what the drive holds now and the card it was last found on.</summary>
    /// <param name="appId">The shortcut, for a new binding.</param>
    /// <param name="path">The literal path.</param>
    /// <param name="probe">The shared probe's answer for that path, through the drive as it is now.</param>
    /// <param name="atDrive">The volume at the path's drive now, or null when nothing is mounted there.</param>
    /// <param name="binding">The card the path was last found on, or null.</param>
    /// <param name="boundVolume">That card when it is mounted anywhere right now, or null.</param>
    /// <returns>
    ///     Available, rebinding to the card that holds the file now. A bound card that is out, or another card in
    ///     its drive, is LibraryMissing naming the bound card; the bound card present without the file is
    ///     FileMissing. Never bound: a removable drive without the file is LibraryMissing, a fixed disk FileMissing.
    /// </returns>
    public static LiteralVerdict DecideLiteral(uint appId, string path, ContentPathState probe,
        ObservedVolume? atDrive, ShortcutBinding? binding, ObservedVolume? boundVolume)
    {
        var drive = Path.GetPathRoot(path) ?? path;
        var letter = drive.TrimEnd('\\', '/');
        if (probe == ContentPathState.Present)
        {
            if (atDrive is null)
            {
                return new LiteralVerdict(ContentAvailability.Available, letter, "", binding);
            }

            var found = atDrive.VolumeId.Length > 0
                ? new ShortcutBinding(binding?.AccountId ?? 0, appId, path, atDrive.VolumeId, atDrive.CardId, atDrive.Name)
                : null;
            return new LiteralVerdict(ContentAvailability.Available, atDrive.Name, "", found);
        }

        if (probe == ContentPathState.Unreadable)
        {
            var name = atDrive?.Name ?? binding?.Name ?? letter;
            return new LiteralVerdict(ContentAvailability.LibraryMissing, name,
                $"{name} in {letter} could not be read. Check it, then choose Recheck.", binding);
        }

        if (binding is not null)
        {
            if (atDrive is not null && atDrive.Holds(binding))
            {
                return new LiteralVerdict(ContentAvailability.FileMissing, binding.Name,
                    $"{path} is missing from {binding.Name}. Edit the shortcut in Steam if the game moved.",
                    binding with { VolumeId = atDrive.VolumeId, Name = atDrive.Name });
            }

            return new LiteralVerdict(ContentAvailability.LibraryMissing, binding.Name,
                boundVolume is not null
                    ? $"{binding.Name} is in {boundVolume.Mount.TrimEnd('\\', '/')}, but this shortcut starts from {letter}. Give it that drive letter again."
                    : $"Insert {binding.Name}.",
                binding);
        }

        if (atDrive is null)
        {
            return new LiteralVerdict(ContentAvailability.LibraryMissing, letter,
                $"Connect the storage for drive {letter} and choose Recheck.", null);
        }

        return atDrive.Removable
            ? new LiteralVerdict(ContentAvailability.LibraryMissing, letter,
                $"Not on the card in {letter}. Insert the card that holds it.", null)
            : new LiteralVerdict(ContentAvailability.FileMissing, atDrive.Name,
                $"{path} is missing. Edit the shortcut in Steam if the game moved.", null);
    }

    private static ManagedContentPath Literal(string path, bool directory)
    {
        return new ManagedContentPath { AbsolutePath = path, Directory = directory };
    }

    private static bool IsHelper(string program)
    {
        return LocalAbsolute(program) && string.Equals(Path.GetFileName(program),
            PackagedLauncherShortcut.ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a value is a drive-rooted path with nothing a path cannot hold.</summary>
    private static bool LocalAbsolute(string path)
    {
        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
               && path.IndexOfAny(['"', '<', '>', '|', '?', '*', '\0']) < 0 && path.IndexOf(':', 2) < 0;
    }
}
