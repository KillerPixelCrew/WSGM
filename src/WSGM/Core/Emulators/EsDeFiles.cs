using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WSGM.Core;

/// <summary>
///     The files WSGM keeps for ES-DE: the ROM folder of links, ES-DE's custom systems file and its ROM folder
///     setting. Nothing else in ES-DE's data is written, and nothing is ever written into a ROM folder.
/// </summary>
internal static class EsDeFiles
{
    /// <summary>The first line of the systems file's comment, which marks it as WSGM's.</summary>
    internal const string Marker = "Written by WSGM";

    private const long MaximumConfigurationBytes = 4 * 1024 * 1024;

    private static readonly Regex RomDirectory = new("<string\\s+name=\"ROMDirectory\"\\s+value=\"([^\"]*)\"\\s*/>",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The folder of links ES-DE reads as its ROM folder, beside the BIOS folder.</summary>
    /// <param name="userRoot">The user's WSGM data root.</param>
    internal static string RomFolder(string userRoot)
    {
        return Path.Combine(userRoot, "Emulation", "es-de-roms");
    }

    /// <summary>ES-DE's custom systems file inside its application data.</summary>
    internal static string SystemsPath(string dataPath)
    {
        return Path.Combine(dataPath, "custom_systems", "es_systems.xml");
    }

    /// <summary>ES-DE's settings file inside its application data.</summary>
    internal static string SettingsPath(string dataPath)
    {
        return Path.Combine(dataPath, "settings", "es_settings.xml");
    }

    /// <summary>Reads the installed ES-DE's own Windows systems: their names, shown names, platforms and themes.</summary>
    /// <param name="programDirectory">The folder holding <c>ES-DE.exe</c>.</param>
    /// <returns>The systems by short name; empty when the file is missing or unreadable.</returns>
    internal static IReadOnlyDictionary<string, EsDeBundledSystem> ReadBundled(string programDirectory)
    {
        var path = Path.Combine(programDirectory, "resources", "systems", "windows", "es_systems.xml");
        Dictionary<string, EsDeBundledSystem> systems = new(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumConfigurationBytes)
            {
                return systems;
            }

            using var stream = File.OpenRead(path);
            var document = XDocument.Load(stream);
            foreach (var system in document.Root?.Elements("system") ?? [])
            {
                var name = system.Element("name")?.Value.Trim() ?? "";
                if (name.Length == 0 || systems.ContainsKey(name))
                {
                    continue;
                }

                var platform = system.Element("platform")?.Value.Trim() ?? "";
                var theme = system.Element("theme")?.Value.Trim() ?? "";
                systems[name] = new EsDeBundledSystem(name, system.Element("fullname")?.Value.Trim() ?? name,
                    platform.Length > 0 ? platform : name, theme.Length > 0 ? theme : name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            Log.Warn("ES-DE's own systems could not be read: " + ex.Message);
            systems.Clear();
        }

        return systems;
    }

    /// <summary>
    ///     Every ROM folder with its system: a source of one system as it is, and the system folders of a source set to
    ///     detect them, found the way the importer finds them.
    /// </summary>
    /// <param name="sources">The configured ROM sources.</param>
    /// <param name="profiles">WSGM's systems, including those installed cores add.</param>
    /// <returns>The folders; a disconnected source of one system has an empty path.</returns>
    internal static List<EsDeFolder> Folders(IEnumerable<RomSourceConfig> sources,
        IReadOnlyList<RomSystemProfile> profiles)
    {
        List<EsDeFolder> folders = [];
        foreach (var source in sources)
        {
            var root = "";
            try
            {
                root = source.ResolveRoot();
                root = Directory.Exists(root) ? root : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or ArgumentException)
            {
                // The storage is not connected; its folder stays listed without a path.
            }

            var systemId = EmulatorStorage.NormalizeSystemId(source.SystemId);
            if (systemId != RomProfiles.Automatic.Id)
            {
                if (profiles.FirstOrDefault(profile => profile.Id == systemId) is { } profile)
                {
                    folders.Add(new EsDeFolder(source.Id, source.Name, systemId, root, Extensions(source, profile)));
                }

                continue;
            }

            if (root.Length == 0)
            {
                // Which systems a disconnected collection holds is unknown until it is read again.
                continue;
            }

            try
            {
                // The importer's own detection. Its skipped systems are a Steam choice: ES-DE shows every
                // system the library holds.
                foreach (var folder in RomLibrarySource.SystemFolders(root, profiles))
                {
                    folders.Add(new EsDeFolder(source.Id, source.Name, folder.SystemId, folder.Folder,
                        Extensions(source, profiles.First(profile => profile.Id == folder.SystemId))));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"ES-DE: the ROM folder {source.Name} could not be read: {ex.Message}");
            }
        }

        return folders;
    }

    /// <summary>The custom systems file for a plan: the systems ES-DE shows, each with its commands.</summary>
    internal static string SystemsXml(EsDePlan plan)
    {
        var list = new XElement("systemList");
        foreach (var system in plan.Systems.Where(item => item.Shown))
        {
            list.Add(new XElement("system",
                new XElement("name", system.Name),
                new XElement("fullname", system.FullName),
                new XElement("path", "%ROMPATH%\\" + system.Name),
                new XElement("extension", string.Join(" ", system.Extensions)),
                system.Commands.Select(command =>
                    new XElement("command", new XAttribute("label", command.Label), command.Command)),
                new XElement("platform", system.Platform),
                new XElement("theme", system.Theme)));
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XComment(" " + Marker + " from your ROM folders and installed emulators. WSGM replaces this file "
                         + "whenever they change: choose emulators in WSGM's Emulator Manager, and each game's "
                         + "alternative emulator in ES-DE. "),
            list);
        StringBuilder text = new();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings
               {
                   Indent = true, IndentChars = "    ", NewLineChars = "\n", OmitXmlDeclaration = true
               }))
        {
            document.Save(writer);
        }

        return "<?xml version=\"1.0\"?>\n" + text + "\n";
    }

    /// <summary>
    ///     Writes ES-DE's custom systems file when it differs. A file WSGM did not write is kept once beside it as
    ///     <c>es_systems.user.xml</c> before it is replaced.
    /// </summary>
    /// <param name="dataPath">ES-DE's application data folder.</param>
    /// <param name="plan">The systems to write.</param>
    /// <returns>Whether the file changed.</returns>
    internal static bool WriteSystems(string dataPath, EsDePlan plan)
    {
        var path = SystemsPath(dataPath);
        var text = SystemsXml(plan);
        if (File.Exists(path))
        {
            var current = new FileInfo(path).Length <= MaximumConfigurationBytes ? File.ReadAllText(path) : "";
            if (current == text)
            {
                return false;
            }

            var kept = Path.Combine(Path.GetDirectoryName(path)!, "es_systems.user.xml");
            if (!current.Contains(Marker, StringComparison.Ordinal) && !File.Exists(kept))
            {
                File.Copy(path, kept);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteText(path, text, true);
        return true;
    }

    /// <summary>
    ///     Points ES-DE's ROM folder setting at WSGM's folder of links, changing that one entry and preserving every
    ///     other setting. A folder the user chose in ES-DE is replaced only when <paramref name="replace" /> asks.
    /// </summary>
    /// <param name="dataPath">ES-DE's application data folder.</param>
    /// <param name="romFolder">WSGM's ES-DE ROM folder.</param>
    /// <param name="replace">Whether to replace a different folder the user chose.</param>
    /// <returns>What the setting holds now.</returns>
    internal static EsDeRomFolderSetting SetRomDirectory(string dataPath, string romFolder, bool replace)
    {
        var path = SettingsPath(dataPath);
        var entry = "<string name=\"ROMDirectory\" value=\"" + SecurityElement.Escape(romFolder) + "\" />";
        if (!File.Exists(path))
        {
            // ES-DE fills in every other setting with its default when it first starts.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteText(path, "<?xml version=\"1.0\"?>\n" + entry + "\n", true);
            return EsDeRomFolderSetting.Set;
        }

        if (new FileInfo(path).Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException("ES-DE's settings file is too large to edit.");
        }

        var text = File.ReadAllText(path);
        var match = RomDirectory.Match(text);
        string changed;
        if (match.Success)
        {
            var current = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (SamePath(current, romFolder))
            {
                return EsDeRomFolderSetting.Unchanged;
            }

            if (current.Trim().Length > 0 && !replace)
            {
                return EsDeRomFolderSetting.Elsewhere;
            }

            changed = text[..match.Index] + entry + text[(match.Index + match.Length)..];
        }
        else
        {
            var declaration = text.StartsWith("<?xml", StringComparison.Ordinal) ? text.IndexOf("?>", StringComparison.Ordinal) : -1;
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            changed = declaration < 0
                ? entry + newline + text
                : text[..(declaration + 2)] + newline + entry + text[(declaration + 2)..];
        }

        AtomicFile.WriteText(path, changed, true);
        return EsDeRomFolderSetting.Set;
    }

    /// <summary>Reads the ROM folder ES-DE's settings name, or empty when none is set.</summary>
    internal static string ReadRomDirectory(string dataPath)
    {
        var path = SettingsPath(dataPath);
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumConfigurationBytes)
        {
            return "";
        }

        var match = RomDirectory.Match(File.ReadAllText(path));
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : "";
    }

    /// <summary>
    ///     Makes the ROM folder hold exactly the planned links. Links that are no longer planned, or point elsewhere,
    ///     are removed as links only; their targets are never entered. A link whose folder is disconnected is kept.
    ///     Files and folders someone else put there are left as they are and reported.
    /// </summary>
    /// <param name="root">WSGM's ES-DE ROM folder.</param>
    /// <param name="links">The planned links.</param>
    /// <param name="createLink">Creates one directory link, the seam tests use.</param>
    /// <returns>What could not be arranged, worded for the user; empty when everything is in place.</returns>
    internal static List<string> ReconcileLinks(string root, IReadOnlyList<EsDeLink> links,
        Action<string, string> createLink)
    {
        Directory.CreateDirectory(root);
        List<string> problems = [];
        var wanted = links.ToDictionary(link => link.Path, link => link.Target, StringComparer.OrdinalIgnoreCase);
        var parents = links.Select(link => link.Path.Split(Path.DirectorySeparatorChar)).Where(parts => parts.Length > 1)
            .Select(parts => parts[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Keep(DirectoryInfo link, string relative)
        {
            return wanted.TryGetValue(relative, out var target)
                   && (target.Length == 0 || EmulatorPortable.Same(link.LinkTarget!, target));
        }

        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                if (entry is not DirectoryInfo directory || !Keep(directory, entry.Name))
                {
                    RemoveLink(entry);
                }

                continue;
            }

            if (entry is not DirectoryInfo folder)
            {
                continue;
            }

            foreach (var child in folder.EnumerateFileSystemInfos())
            {
                if (child.LinkTarget is not null
                    && (child is not DirectoryInfo linked || !Keep(linked, Path.Combine(folder.Name, child.Name))))
                {
                    RemoveLink(child);
                }
            }

            if (!parents.Contains(folder.Name))
            {
                if (folder.EnumerateFileSystemInfos().Any())
                {
                    problems.Add(folder.FullName + " holds files of its own, so WSGM left it as it is.");
                }
                else
                {
                    folder.Delete();
                }
            }
        }

        foreach (var link in links.Where(item => item.Target.Length > 0))
        {
            var path = Path.Combine(root, link.Path);
            if (new DirectoryInfo(path).LinkTarget is not null || Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            try
            {
                var parent = Path.GetDirectoryName(path)!;
                if (!EmulatorPortable.Same(parent, root))
                {
                    if (new DirectoryInfo(parent).LinkTarget is not null || File.Exists(parent))
                    {
                        problems.Add(parent + " is in the way of the folder WSGM needs there.");
                        continue;
                    }

                    Directory.CreateDirectory(parent);
                }

                createLink(path, link.Target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(link.Target + " could not be shown to ES-DE: " + ex.Message);
            }
        }

        return problems;
    }

    private static List<string> Extensions(RomSourceConfig source, RomSystemProfile profile)
    {
        return source.Extensions.Count > 0
            ? GameLibraryRules.NormalizeExtensions(source.Extensions)
            : [.. profile.Extensions];
    }

    /// <summary>Removes a directory link or a file link without entering its target.</summary>
    private static void RemoveLink(FileSystemInfo link)
    {
        if (link is DirectoryInfo)
        {
            Directory.Delete(link.FullName);
        }
        else
        {
            File.Delete(link.FullName);
        }
    }

    /// <summary>Whether a setting names WSGM's folder; ES-DE's own variables never do.</summary>
    internal static bool SamePath(string left, string right)
    {
        try
        {
            return left.Trim().Length > 0 && EmulatorPortable.Same(left.Trim(), right);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A value such as %ESPATH%\ROMs is ES-DE's own, not WSGM's folder.
            return false;
        }
    }
}
