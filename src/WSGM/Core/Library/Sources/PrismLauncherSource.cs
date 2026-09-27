using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Finds the Minecraft instances Prism Launcher manages.</summary>
/// <remarks>
///     <para>
///         Prism starts an instance with <c>--launch &lt;instance id&gt;</c>, where the id is the
///         instance's folder name. The shortcut therefore runs Prism itself, and Steam tracks Prism
///         rather than the game's Java process, which is also what Steam ROM Manager's MultiMC preset
///         does.
///     </para>
///     <para>
///         Prism keeps its data beside the executable when a <c>portable.txt</c> sits there, and in
///         <c>%APPDATA%\PrismLauncher</c> otherwise. The instances folder can be moved in Prism's own
///         settings, so it is read from <c>prismlauncher.cfg</c>.
///     </para>
/// </remarks>
public sealed class PrismLauncherSource : ILibrarySource
{
    private const string ExecutableName = "prismlauncher.exe";

    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, IReadOnlyList<string>> _enumerateDirectories;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<Environment.SpecialFolder, string> _folder;
    private readonly Func<string, string?> _readText;
    private readonly Func<IReadOnlyList<UninstallEntry>> _uninstallEntries;

    /// <summary>Creates the source over the real registry and file system.</summary>
    public PrismLauncherSource()
        : this(UninstallEntries.Read, Environment.GetFolderPath, File.Exists, Directory.Exists,
            LibraryFiles.Directories, LibraryFiles.ReadText)
    {
    }

    /// <summary>Creates the source over injected discovery seams.</summary>
    /// <param name="uninstallEntries">Reads Windows' installed-programs list.</param>
    /// <param name="folder">Resolves a special folder, such as local application data.</param>
    /// <param name="fileExists">Whether a file exists.</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    /// <param name="enumerateDirectories">Lists a folder's subfolders as full paths, empty when unreadable.</param>
    /// <param name="readText">Reads a file's text, or returns null when it cannot be read.</param>
    internal PrismLauncherSource(
        Func<IReadOnlyList<UninstallEntry>> uninstallEntries,
        Func<Environment.SpecialFolder, string> folder,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, IReadOnlyList<string>> enumerateDirectories,
        Func<string, string?> readText)
    {
        ArgumentNullException.ThrowIfNull(uninstallEntries);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(enumerateDirectories);
        ArgumentNullException.ThrowIfNull(readText);
        _uninstallEntries = uninstallEntries;
        _folder = folder;
        _fileExists = fileExists;
        _directoryExists = directoryExists;
        _enumerateDirectories = enumerateDirectories;
        _readText = readText;
    }

    /// <inheritdoc />
    public string Id => "prism";

    /// <inheritdoc />
    public string DisplayName => "Prism Launcher";

    /// <inheritdoc />
    public SourceAvailability Detect()
    {
        return FindExecutable() is null ? SourceAvailability.NotFound : new SourceAvailability(true, "Installed");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(cancellationToken), cancellationToken);
    }

    private IReadOnlyList<DiscoveredGame> Discover(CancellationToken cancellationToken)
    {
        var executable = FindExecutable();
        if (executable is null)
        {
            return [];
        }

        var instances = InstancesFolder(executable);
        if (!_directoryExists(instances))
        {
            return [];
        }

        var start = Path.GetDirectoryName(executable) ?? string.Empty;
        List<DiscoveredGame> found = [];
        foreach (var directory in _enumerateDirectories(instances).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(directory);
            // Prism keeps its own scratch folders here, named with a leading underscore or dot.
            if (folderName.Length == 0 || folderName.StartsWith('_') || folderName.StartsWith('.'))
            {
                continue;
            }

            var config = _readText(Path.Combine(directory, "instance.cfg"));
            if (config is null)
            {
                continue;
            }

            var name = LibraryFiles.QtIniValue(config, "name");
            ShortcutRoute route = new(
                "launcher",
                "Prism Launcher",
                executable,
                start,
                $"--launch \"{folderName}\"",
                "Prism Launcher starts this instance. WSGM follows the instance's Java process, so Steam shows "
                + "the game running for as long as it is.",
                FollowMarker: directory);
            found.Add(new DiscoveredGame(
                Id,
                folderName,
                string.IsNullOrWhiteSpace(name) ? folderName : name.Trim(),
                directory,
                new GameLaunch(route.Label, true, route.Evidence),
                MultiplayerVerdict.Unknown,
                "The launcher does not say.",
                true,
                [],
                [],
                [route]));
        }

        return found;
    }

    /// <summary>Where Prism keeps its instances, following its configuration.</summary>
    private string InstancesFolder(string executable)
    {
        var programFolder = Path.GetDirectoryName(executable) ?? string.Empty;
        var data = _fileExists(Path.Combine(programFolder, "portable.txt"))
            ? programFolder
            : Path.Combine(_folder(Environment.SpecialFolder.ApplicationData), "PrismLauncher");
        var configured = _readText(Path.Combine(data, "prismlauncher.cfg")) is { } config
            ? LibraryFiles.QtIniValue(config, "InstanceDir")
            : null;
        var instances = string.IsNullOrWhiteSpace(configured) ? "instances" : configured.Trim();
        try
        {
            // Qt writes forward slashes; Path.Combine keeps an absolute setting as it is.
            return Path.GetFullPath(Path.Combine(data, instances.Replace('/', '\\')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.Combine(data, "instances");
        }
    }

    /// <summary>The installed executable, or null when Prism is not on this machine.</summary>
    private string? FindExecutable()
    {
        foreach (var entry in _uninstallEntries())
        {
            if (!entry.DisplayName.StartsWith("Prism Launcher", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.InstallLocation.Length > 0
                && Existing(Path.Combine(entry.InstallLocation, ExecutableName)) is { } installed)
            {
                return installed;
            }

            // Some installers leave InstallLocation empty but name the executable as the icon.
            var icon = entry.DisplayIcon.Split(',')[0].Trim().Trim('"');
            if (icon.EndsWith(ExecutableName, StringComparison.OrdinalIgnoreCase) && Existing(icon) is { } shown)
            {
                return shown;
            }
        }

        var local = _folder(Environment.SpecialFolder.LocalApplicationData);
        return local.Length == 0
            ? null
            : Existing(Path.Combine(local, "Programs", "PrismLauncher", ExecutableName));
    }

    private string? Existing(string path)
    {
        return _fileExists(path) ? path : null;
    }
}
