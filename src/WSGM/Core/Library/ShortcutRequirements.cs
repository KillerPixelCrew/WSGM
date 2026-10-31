using System;
using System.Collections.Generic;
using System.IO;

namespace WSGM.Core;

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
