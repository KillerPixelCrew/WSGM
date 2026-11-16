using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>
///     Decides what ES-DE is given: one system per system the ROM folders hold, each launch command built from the
///     installed emulators' own launch arguments with the System default first, and the links that show the folders.
/// </summary>
/// <remarks>
///     ES-DE starts the emulators itself, so a command carries the active version's absolute paths and WSGM's
///     arguments, with ES-DE's game variables where WSGM's launcher would put the game. Pure: the caller reads the
///     folders, the installations and ES-DE's own system names, and writes the result.
/// </remarks>
internal static class EsDeConfiguration
{
    /// <summary>The catalogue definition WSGM configures.</summary>
    internal const string DefinitionId = "es-de";

    // File types only WSGM's own launcher can start: it opens a PS2 playlist's first disc and refuses
    // compressed Switch content with an explanation. ES-DE would hand either straight to the emulator.
    private static readonly Dictionary<string, string[]> LauncherOnlyExtensions = new(StringComparer.Ordinal)
    {
        ["ps2"] = [".m3u"],
        ["switch"] = [".nsz", ".xcz"]
    };

    /// <summary>ES-DE's short name for a WSGM system, or empty when ES-DE has no such system.</summary>
    /// <param name="systemId">The system, aliases included.</param>
    /// <returns>The name of ES-DE's system and of its folder under the ROM folder.</returns>
    internal static string SystemName(string systemId)
    {
        return EmulatorStorage.NormalizeSystemId(systemId) switch
        {
            "gamecube" => "gc",
            "3ds" => "n3ds",
            "jaguar" => "atarijaguar",
            "jaguarcd" => "atarijaguarcd",
            // ES-DE has no Sega Pico system.
            "pico" => "",
            var id => id
        };
    }

    /// <summary>Builds ES-DE's systems and links from the current folders and installations.</summary>
    /// <param name="folders">Every ROM folder with its system; a disconnected folder has an empty path.</param>
    /// <param name="store">The installations and the effective system preferences.</param>
    /// <param name="profiles">WSGM's systems, which order the result and decide how games are found.</param>
    /// <param name="bundled">The installed ES-DE's own systems by name, or empty when they could not be read.</param>
    /// <returns>The systems, shown or left out with the reason, and the links of the shown ones.</returns>
    internal static EsDePlan Build(IReadOnlyList<EsDeFolder> folders, EmulatorStore store,
        IReadOnlyList<RomSystemProfile> profiles, IReadOnlyDictionary<string, EsDeBundledSystem> bundled)
    {
        var order = profiles.Select((profile, index) => (profile.Id, index))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        var systems = folders.GroupBy(folder => EmulatorStorage.NormalizeSystemId(folder.SystemId),
                StringComparer.Ordinal)
            .OrderBy(group => order.GetValueOrDefault(group.Key, int.MaxValue))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => System(group.Key, [.. group], store,
                profiles.FirstOrDefault(profile => profile.Id == group.Key), bundled))
            .ToArray();
        List<EsDeLink> links = [];
        foreach (var system in systems.Where(item => item.Shown))
        {
            // The layout follows every folder, connected or not, so a card coming and going never reshapes it.
            if (system.Folders.Length == 1)
            {
                links.Add(new EsDeLink(system.Name, system.Folders[0].Path));
                continue;
            }

            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in system.Folders)
            {
                var name = FolderName(folder.SourceName);
                var unique = name;
                for (var number = 2; !names.Add(unique); number++)
                {
                    unique = name + " (" + number + ")";
                }

                links.Add(new EsDeLink(Path.Combine(system.Name, unique), folder.Path));
            }
        }

        return new EsDePlan(systems, [.. links]);
    }

    /// <summary>The launch commands for one system: the System default first, then the other emulators and cores.</summary>
    /// <param name="store">The installations and the effective system preferences.</param>
    /// <param name="systemId">The canonical system.</param>
    /// <param name="excluded">The emulators that run the system but cannot be started by ES-DE.</param>
    /// <returns>The commands, with labels that are unique within the system.</returns>
    internal static EsDeCommand[] Commands(EmulatorStore store, string systemId, out string[] excluded)
    {
        var preferred = ManagedContentStorage.PreferredSystem(store, systemId);
        List<(EmulatorInstallation Installation, EmulatorCore? Core)> candidates = [];
        foreach (var installation in store.Installations.Where(item => EmulatorStorage.SupportsSystem(item, systemId)))
        {
            if (!installation.DataPolicy.HasCores)
            {
                candidates.Add((installation, null));
                continue;
            }

            // A core is offered only when its information names the system; an unknown core is never guessed.
            candidates.AddRange(EmulatorStorage.CompatibleCores(installation, systemId)
                .Where(core => !core.MetadataMissing && core.Systems.Length > 0)
                .Select(core => (installation, (EmulatorCore?)core)));
        }

        var ordered = candidates
            .OrderByDescending(candidate => preferred is not null
                                            && candidate.Installation.Id == preferred.InstallationId
                                            && (candidate.Core?.Id ?? "") == preferred.CoreId)
            .ThenBy(candidate => candidate.Core is not null)
            .ThenBy(candidate => candidate.Installation.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Core?.Name ?? "", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Installation.Id, StringComparer.Ordinal)
            .ToArray();
        var shared = candidates.GroupBy(candidate => candidate.Installation.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(candidate => candidate.Installation.Id).Distinct().Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<EsDeCommand> commands = [];
        HashSet<string> labels = new(StringComparer.Ordinal);
        HashSet<string> unusable = new(StringComparer.CurrentCulture);
        foreach (var (installation, core) in ordered)
        {
            var command = CommandLine(installation, core);
            if (command is null)
            {
                unusable.Add(installation.Name);
                continue;
            }

            var label = core?.Name ?? installation.Name;
            if (shared.Contains(installation.Name))
            {
                label += " (" + (installation.Managed ? installation.Channel : "your installation") + ")";
            }

            var unique = label;
            for (var number = 2; !labels.Add(unique); number++)
            {
                unique = label + " " + number;
            }

            commands.Add(new EsDeCommand(unique, command, installation.Id, core?.Id ?? ""));
        }

        excluded = [.. unusable];
        return [.. commands];
    }

    /// <summary>
    ///     The command ES-DE runs for one emulator and core: the start folder WSGM's launcher uses, the executable and
    ///     the installation's launch arguments, with ES-DE's variables for the game.
    /// </summary>
    /// <returns>The command, or null when ES-DE cannot start this emulator as WSGM's launcher would.</returns>
    internal static string? CommandLine(EmulatorInstallation installation, EmulatorCore? core)
    {
        // Model 2 takes a driver name and finds its ROMs through EMULATOR.INI, which WSGM's launcher edits per
        // game; an emulator with its own environment needs a launcher that sets it. Neither has an ES-DE form.
        if (installation.DefinitionId == "model2" || installation.Environment.Count > 0
                                                  || installation.LaunchArguments.Length == 0
                                                  || !installation.LaunchArguments.Any(argument =>
                                                      argument.Contains("{rom", StringComparison.Ordinal)))
        {
            return null;
        }

        List<string> parts = [];
        var program = Path.GetDirectoryName(installation.ExecutablePath) ?? "";
        if (!Literal(program) || !Literal(installation.ExecutablePath))
        {
            return null;
        }

        // ES-DE reads a literal emulator path from the command's first word, before it removes its own
        // variables, so the start folder follows the executable. It is the folder WSGM's own launch uses:
        // MAME, for one, reads its settings from it.
        parts.Add(WindowsCommandLine.Quote(installation.ExecutablePath));
        parts.Add("%STARTDIR%=" + (program.Contains(' ') ? "\"" + program + "\"" : program));
        foreach (var argument in installation.LaunchArguments)
        {
            if (argument == "{rom}")
            {
                // ES-DE quotes the game's path itself.
                parts.Add("%ROM%");
                continue;
            }

            var expanded = EmulatorStorage.ExpandArgument(argument, installation, core, "\u0001ROM\u0002",
                "\u0001BASE\u0002", "\u0001DIR\u0002");
            var game = expanded.Contains('\u0001');
            var literal = expanded.Replace("\u0001ROM\u0002", "", StringComparison.Ordinal)
                .Replace("\u0001BASE\u0002", "", StringComparison.Ordinal)
                .Replace("\u0001DIR\u0002", "", StringComparison.Ordinal);
            if (!Literal(literal))
            {
                return null;
            }

            if (!game)
            {
                parts.Add(WindowsCommandLine.Quote(expanded));
                continue;
            }

            // Inside a longer argument the unescaped variables are used and the whole argument is quoted.
            parts.Add("\"" + expanded.Replace("\u0001ROM\u0002", "%ROMRAW%", StringComparison.Ordinal)
                .Replace("\u0001BASE\u0002", "%BASENAME%", StringComparison.Ordinal)
                .Replace("\u0001DIR\u0002", "%GAMEDIRRAW%", StringComparison.Ordinal) + "\"");
        }

        return string.Join(" ", parts);
    }

    private static EsDeSystem System(string systemId, EsDeFolder[] folders, EmulatorStore store,
        RomSystemProfile? profile, IReadOnlyDictionary<string, EsDeBundledSystem> bundled)
    {
        var name = SystemName(systemId);
        var info = bundled.GetValueOrDefault(name)
                   ?? new EsDeBundledSystem(name, profile?.Name ?? systemId, name, name);
        EsDeSystem LeftOut(string problem)
        {
            return new EsDeSystem(systemId, name, info.FullName, info.Platform, info.Theme, [], [], folders, problem);
        }

        if (name.Length == 0 || (bundled.Count > 0 && !bundled.ContainsKey(name)))
        {
            return LeftOut("ES-DE has no system for these games.");
        }

        if (profile?.ParserKind is RomParserKind.Eboot or RomParserKind.InstalledEboot)
        {
            return LeftOut("ES-DE can't find games installed this way. They stay available as Steam shortcuts.");
        }

        var commands = Commands(store, systemId, out var excluded);
        if (commands.Length == 0)
        {
            return LeftOut(excluded.Length > 0
                ? EmulatorPresentation.JoinAnd(excluded) + " starts only from WSGM's Steam shortcuts."
                : "Install an emulator for this system.");
        }

        var dropped = LauncherOnlyExtensions.GetValueOrDefault(systemId) ?? [];
        string[] extensions =
        [
            .. folders.SelectMany(folder => folder.Extensions)
                .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
                .Select(extension => extension.ToLowerInvariant())
                .Where(extension => extension.Length > 1 && !dropped.Contains(extension, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .SelectMany(extension => new[] { extension, extension.ToUpperInvariant() })
        ];
        return new EsDeSystem(systemId, name, info.FullName, info.Platform, info.Theme, extensions, commands, folders,
            extensions.Length == 0 ? "No file types are chosen for these games." : "");
    }

    /// <summary>A link name from a source's name: the characters a folder name allows, or Folder.</summary>
    private static string FolderName(string sourceName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. sourceName.Select(character => invalid.Contains(character) ? '_' : character)])
            .Trim().TrimEnd('.');
        return name.Length == 0 ? "Folder" : name;
    }

    /// <summary>Whether text can stand in an ES-DE command as written: ES-DE expands <c>%</c> and splits on quotes.</summary>
    private static bool Literal(string value)
    {
        return value.IndexOfAny(['%', '"']) < 0;
    }
}
