using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Finds the Minecraft instances ATLauncher manages.</summary>
/// <remarks>
///     <para>
///         ATLauncher starts an instance with <c>--launch &lt;instance&gt;</c> and closes itself. The
///         shortcut runs ATLauncher through WSGM's follow launcher, which follows the Java process whose
///         command line names the instance's folder, so Steam tracks the game rather than ATLauncher.
///     </para>
///     <para>
///         ATLauncher matches the argument against an instance's name or its safe name, the name with
///         everything but ASCII letters and digits removed, without regard to case (its <c>App.java</c>
///         auto-launch lookup). The safe name is also the folder name it gives a new instance.
///         The name itself is passed when it can be: when it has no quote to break the argument and
///         only ASCII characters, because Java reads its command line in the system's ANSI code page and
///         a character outside it would arrive as <c>?</c>. Otherwise the safe name is passed, and the
///         folder name when nothing of the name is left.
///     </para>
///     <para>
///         ATLauncher is portable by default and keeps its data beside the executable. The installer
///         puts both in <c>%APPDATA%\ATLauncher</c>.
///     </para>
/// </remarks>
public sealed class AtLauncherSource : ILibrarySource
{
    private const string ExecutableName = "ATLauncher.exe";

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, IReadOnlyList<string>> _enumerateDirectories;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<Environment.SpecialFolder, string> _folder;
    private readonly Func<string, string?> _readText;

    /// <summary>Creates the source over the real registry and file system.</summary>
    public AtLauncherSource()
        : this(Environment.GetFolderPath, File.Exists, Directory.Exists, LibraryFiles.Directories,
            LibraryFiles.ReadText)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="folder">Resolves a special folder, such as roaming application data.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="enumerateDirectories">Lists a folder's subfolders as full paths, empty when unreadable.</param>
    /// <param name="readText">Reads a file's text, or returns null when it cannot be read.</param>
    internal AtLauncherSource(
        Func<Environment.SpecialFolder, string> folder,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, IReadOnlyList<string>> enumerateDirectories,
        Func<string, string?> readText)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(enumerateDirectories);
        ArgumentNullException.ThrowIfNull(readText);
        _folder = folder;
        _fileExists = fileExists;
        _directoryExists = directoryExists;
        _enumerateDirectories = enumerateDirectories;
        _readText = readText;
    }

    /// <inheritdoc />
    public string Id => "atlauncher";

    /// <inheritdoc />
    public string DisplayName => "ATLauncher";

    /// <inheritdoc />
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        return FindExecutable(programs) is null
            ? SourceAvailability.NotFound
            : new SourceAvailability(true, "Installed");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(programs, cancellationToken), cancellationToken);
    }

    /// <summary>The name ATLauncher matches <c>--launch</c> against: letters and digits only.</summary>
    /// <param name="name">The instance's display name.</param>
    /// <returns>The safe name, empty when nothing is left.</returns>
    internal static string SafeName(string name)
    {
        StringBuilder safe = new(name.Length);
        foreach (var character in name)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                safe.Append(character);
            }
        }

        return safe.ToString();
    }

    /// <summary>What to pass <c>--launch</c> for an instance.</summary>
    /// <param name="name">The instance's display name, trimmed, or null when unreadable.</param>
    /// <param name="folderName">The instance's folder name.</param>
    /// <returns>The name when it survives the command line, else its safe name, else the folder name.</returns>
    internal static string LaunchArgument(string? name, string folderName)
    {
        if (string.IsNullOrEmpty(name))
        {
            return folderName;
        }

        if (Ascii.IsValid(name) && !name.Contains('"') && !name.Any(char.IsControl))
        {
            return name;
        }

        var safe = SafeName(name);
        return safe.Length > 0 ? safe : folderName;
    }

    private IReadOnlyList<DiscoveredGame> Discover(
        IReadOnlyList<UninstallEntry> programs, CancellationToken cancellationToken)
    {
        var executable = FindExecutable(programs);
        if (executable is null)
        {
            return [];
        }

        var programFolder = Path.GetDirectoryName(executable) ?? string.Empty;
        var data = _directoryExists(Path.Combine(programFolder, "instances"))
            ? programFolder
            : Path.Combine(_folder(Environment.SpecialFolder.ApplicationData), "ATLauncher");
        var instances = Path.Combine(data, "instances");
        if (!_directoryExists(instances))
        {
            return [];
        }

        List<DiscoveredGame> found = [];
        foreach (var directory in _enumerateDirectories(instances).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(directory);
            if (folderName.Length == 0 || folderName.StartsWith('.'))
            {
                continue;
            }

            var json = _readText(Path.Combine(directory, "instance.json"));
            if (json is null)
            {
                continue;
            }

            var name = InstanceName(json)?.Trim();
            ShortcutRoute route = new(
                "launcher",
                "ATLauncher",
                executable,
                programFolder,
                LaunchArguments.Named("--launch ", LaunchArgument(name, folderName))
                + " --close-launcher --no-launcher-update",
                "ATLauncher starts this instance and closes. WSGM follows the instance's Java process, so Steam "
                + "shows the game running for as long as it is.",
                FollowMarker: directory);
            found.Add(DiscoveredGame.Command(this,
                folderName,
                string.IsNullOrEmpty(name) ? folderName : name,
                directory,
                [route]));
        }

        return found;
    }

    /// <summary>The instance's display name from <c>launcher.name</c>, or null when absent or unreadable.</summary>
    private static string? InstanceName(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind is JsonValueKind.Object
                   && document.RootElement.TryGetProperty("launcher", out var launcher)
                   && launcher.ValueKind is JsonValueKind.Object
                   && launcher.TryGetProperty("name", out var name)
                   && name.ValueKind is JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            // A half-written instance.json is an instance without a readable name, not an error.
            return null;
        }
    }

    /// <summary>The installed executable, or null when ATLauncher is not on this machine.</summary>
    /// <param name="programs">Windows' installed-programs list.</param>
    private string? FindExecutable(IReadOnlyList<UninstallEntry> programs)
    {
        if (UninstallEntries.FindProgram(
                programs,
                entry => entry.DisplayName.StartsWith("ATLauncher", StringComparison.OrdinalIgnoreCase),
                _fileExists,
                ExecutableName) is { } installed)
        {
            return installed;
        }

        var roaming = _folder(Environment.SpecialFolder.ApplicationData);
        if (roaming.Length > 0 && Existing(Path.Combine(roaming, "ATLauncher", ExecutableName)) is { } portable)
        {
            return portable;
        }

        var local = _folder(Environment.SpecialFolder.LocalApplicationData);
        return local.Length == 0
            ? null
            : Existing(Path.Combine(local, "Programs", "ATLauncher", ExecutableName));
    }

    private string? Existing(string path)
    {
        return _fileExists(path) ? path : null;
    }
}
