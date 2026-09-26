using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WSGM.Core;

/// <summary>
///     Keeps Steam's last-resort guide button chord template equal to the chord layout the user
///     last saved, so the edits survive Steam's own reload.
/// </summary>
/// <remarks>
///     <para>
///         Steam's controller layout editor autosaves the guide button chord layout under
///         <c>Steam Controller Configs\&lt;account&gt;\config\443510\</c> (443510 is the chord
///         pseudo-app), as <c>controller_neptune.vdf</c> or as a file named after the pad's vendor,
///         product and serial, then restarts its edit session because the selection
///         changed. For a Steam Deck type controller that restart ignores the selection and parses
///         <c>controller_base\chord_neptune.vdf</c>, the "last resort" template, three to four seconds
///         after the save. Every edit therefore vanished from the editor and was written back as
///         defaults on leaving. Valve has had the report since 2022 for both Windows and SteamOS; WSGM
///         is only affected because its managed target is that controller type.
///     </para>
///     <para>
///         Steam reads the template from disk on every edit-session start, measured on the reference
///         Claw on 2026-09-26. So this watches the autosave and copies it over the template within
///         milliseconds of each write, well ahead of the reload. Valve's own file is kept beside it as
///         <c>chord_neptune.vdf.wsgm-original</c> and comes back for the editor's "reset to defaults",
///         which is reported by the Steam UI hook (<c>wsgm.chord-reset</c>), when the mirror stops,
///         and at uninstall. A reset leaves a marker so an older autosave is not mirrored over it at
///         the next start. A Steam client update that replaces the template is noticed by the template
///         no longer carrying an autosave's <c>progenitor</c> line; the backup is refreshed from it.
///     </para>
///     <para>
///         The template is part of Steam's client package, and Steam's bootstrapper checks every
///         package file's size on each start ("Verifying file sizes only" in bootstrap_log.txt; only
///         executables are hashed). A size mismatch makes it reinstall the whole package, about ten
///         seconds on every start (Claw, 2026-09-26). The mirror therefore writes the layout with its
///         indentation stripped and padded with whitespace to exactly Valve's byte count, which the
///         KeyValues parser does not care about. A layout that does not fit even without any
///         whitespace is not mirrored, and says so once in the log.
///     </para>
/// </remarks>
public sealed class SteamGuideChordMirror : IDisposable
{
    /// <summary>Steam's pseudo-app that owns the guide button chord layout.</summary>
    public const int ChordAppId = 443510;

    internal const string TemplateFileName = "chord_neptune.vdf";
    internal const string BackupSuffix = ".wsgm-original";
    internal const string ResetMarkerSuffix = ".wsgm-reset";
    private const string AutosavePattern = "*.vdf";
    private const int MaximumLayoutBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private static readonly Regex NeptuneType = new(
        "\"controller_type\"\\s+\"controller_neptune\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Revision = new(
        "\"revision\"\\s+\"(\\d+)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _configsRoot;
    private readonly Lock _gate = new();
    private readonly string _template;
    private Timer? _debounce;
    private bool _disposed;
    private bool _enabled = true;
    private string? _overflowHash;
    private int _retries;
    private bool _targetActive;
    private FileSystemWatcher? _templateWatcher;
    private FileSystemWatcher? _watcher;

    /// <summary>Creates a mirror over one Steam installation.</summary>
    /// <param name="steamDirectory">Steam's install directory.</param>
    public SteamGuideChordMirror(string steamDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamDirectory);
        _configsRoot = Path.Combine(steamDirectory, "steamapps", "common", "Steam Controller Configs");
        _template = Path.Combine(steamDirectory, "controller_base", TemplateFileName);
    }

    /// <summary>Whether the mirror is watching: the setting is on and a Steam Deck target is active.</summary>
    public bool Active
    {
        get
        {
            lock (_gate)
            {
                return _watcher is not null;
            }
        }
    }

    /// <summary>Raised when <see cref="Active" /> changed.</summary>
    public event Action? Changed;

    /// <summary>Creates a mirror for the installed Steam client, or null when Steam is not installed.</summary>
    /// <returns>The mirror, or null.</returns>
    public static SteamGuideChordMirror? ForInstalledSteam()
    {
        return Steam.InstallDirectory is { } directory ? new SteamGuideChordMirror(directory) : null;
    }

    /// <summary>Puts Valve's template back for one Steam installation and forgets the backup.</summary>
    /// <param name="steamDirectory">Steam's install directory.</param>
    /// <returns>Whether a backup existed and was restored.</returns>
    /// <remarks>The uninstall path; nothing here needs the watcher.</remarks>
    public static bool RestoreInstalledSteam(string steamDirectory)
    {
        using SteamGuideChordMirror mirror = new(steamDirectory);
        return mirror.RestoreDefault(forget: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Apply(false, false);
    }

    /// <summary>Applies the setting and the managed target's state.</summary>
    /// <param name="enabled">The "keep guide button chord edits" setting.</param>
    /// <param name="steamDeckTargetActive">Whether the managed target is a Steam Deck type and active.</param>
    /// <remarks>
    ///     Turning off restores Valve's template: with the mirror gone, Steam would otherwise keep
    ///     loading a layout nothing maintains any more.
    /// </remarks>
    public void Apply(bool enabled, bool steamDeckTargetActive)
    {
        bool changed;
        lock (_gate)
        {
            _enabled = enabled;
            _targetActive = steamDeckTargetActive;
            var wanted = !_disposed && enabled && steamDeckTargetActive;
            if (wanted == (_watcher is not null))
            {
                return;
            }

            if (wanted)
            {
                changed = StartUnderGate();
            }
            else
            {
                StopUnderGate();
                RestoreUnderGate(forget: true);
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Restores Valve's template ahead of the editor's own reload.</summary>
    /// <param name="forget">Whether the backup is deleted afterwards, as at uninstall.</param>
    /// <returns>Whether a backup existed and was restored.</returns>
    public bool RestoreDefault(bool forget = false)
    {
        lock (_gate)
        {
            return RestoreUnderGate(forget);
        }
    }

    /// <summary>Mirrors the newest autosave when it is newer than the last reset and differs from the template.</summary>
    /// <remarks>Public for the watcher's debounce and the tests; idempotent.</remarks>
    public void Reconcile()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is null && !_targetActive)
            {
                return;
            }

            ReconcileUnderGate();
        }
    }

    private bool StartUnderGate()
    {
        if (!Directory.Exists(_configsRoot))
        {
            // Steam creates the tree with the first layout it saves; a later target change or
            // setting change tries again.
            Log.Change("steam-chord-mirror", $"Guide chord mirror waiting: no layouts under '{_configsRoot}'.");
            return false;
        }

        try
        {
            _watcher = new FileSystemWatcher(_configsRoot, AutosavePattern)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _watcher.Changed += OnAutosaveChanged;
            _watcher.Created += OnAutosaveChanged;
            _watcher.Renamed += OnAutosaveChanged;
            _watcher.EnableRaisingEvents = true;
            // Steam's bootstrapper or a client update can put Valve's file back; mirror again then.
            _templateWatcher = new FileSystemWatcher(Path.GetDirectoryName(_template)!, TemplateFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _templateWatcher.Changed += OnTemplateChanged;
            _templateWatcher.Created += OnTemplateChanged;
            _templateWatcher.Renamed += OnTemplateChanged;
            _templateWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Guide chord mirror could not watch '{_configsRoot}': {ex.Message}");
            _templateWatcher?.Dispose();
            _templateWatcher = null;
            _watcher?.Dispose();
            _watcher = null;
            return false;
        }

        Log.Change("steam-chord-mirror", "Guide chord mirror active: Steam's chord template follows the autosave.");
        ReconcileUnderGate();
        return true;
    }

    private void StopUnderGate()
    {
        _debounce?.Dispose();
        _debounce = null;
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
        if (_templateWatcher is not null)
        {
            _templateWatcher.EnableRaisingEvents = false;
            _templateWatcher.Dispose();
            _templateWatcher = null;
        }

        _overflowHash = null;
    }

    private void OnAutosaveChanged(object sender, FileSystemEventArgs e)
    {
        if (!e.FullPath.Contains($"{Path.DirectorySeparatorChar}{ChordAppId}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return;
        }

        QueueReconcile();
    }

    private void OnTemplateChanged(object sender, FileSystemEventArgs e)
    {
        QueueReconcile();
    }

    private void QueueReconcile()
    {
        lock (_gate)
        {
            if (_watcher is null)
            {
                return;
            }

            _retries = 0;
            // Steam writes in more than one step; the copy waits for the last one.
            _debounce ??= new Timer(_ => Reconcile(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void ReconcileUnderGate()
    {
        string? autosave = null;
        string? text = null;
        foreach (var candidate in AutosaveCandidates())
        {
            try
            {
                if (new FileInfo(candidate).Length > MaximumLayoutBytes)
                {
                    Log.Warn($"Guide chord autosave '{candidate}' is larger than a layout can be; ignored.");
                    continue;
                }

                var read = File.ReadAllText(candidate, Encoding.UTF8);
                if (!IsChordLayout(read))
                {
                    continue;
                }

                autosave = candidate;
                text = read;
                break;
            }
            catch (IOException) when (_retries < 5 && _debounce is not null)
            {
                // Still being written. A few more looks, then the next write wins.
                _retries++;
                _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Guide chord autosave '{candidate}' could not be read: {ex.Message}");
            }
        }

        if (autosave is null || text is null)
        {
            return;
        }

        // A reset after this autosave stands; the marker is cleared by the next newer autosave.
        var marker = _template + ResetMarkerSuffix;
        if (File.Exists(marker) && File.GetLastWriteTimeUtc(marker) >= File.GetLastWriteTimeUtc(autosave))
        {
            return;
        }

        try
        {
            EnsureBackupUnderGate();
            var backup = _template + BackupSuffix;
            if (!File.Exists(backup))
            {
                return;
            }

            // Steam's bootstrapper checks the package file's size on every start; see the remarks.
            var size = checked((int)new FileInfo(backup).Length);
            var mirror = FitToSize(text, size);
            var revision = Revision.Match(text) is { Success: true } match ? match.Groups[1].Value : "?";
            if (mirror is null)
            {
                var hash = Hash(text);
                if (hash != _overflowHash)
                {
                    _overflowHash = hash;
                    Log.Warn(
                        $"Guide chord layout (revision {revision}) does not fit Steam's {size}-byte template even " +
                        "without whitespace; not mirrored, or Steam would reinstall its package on every start.");
                }

                return;
            }

            _overflowHash = null;
            if (File.Exists(_template) && File.ReadAllText(_template, Encoding.UTF8) == mirror)
            {
                return;
            }

            var temporary = _template + ".wsgm-tmp";
            File.WriteAllText(temporary, mirror, new UTF8Encoding(false));
            File.Move(temporary, _template, true);
            File.Delete(marker);
            Log.Change("steam-chord-mirror", $"Guide chord layout mirrored into Steam's template (revision {revision}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Guide chord layout could not be mirrored: {ex.Message}");
        }
    }

    private void EnsureBackupUnderGate()
    {
        var backup = _template + BackupSuffix;
        if (!File.Exists(_template))
        {
            return;
        }

        var current = File.ReadAllText(_template, Encoding.UTF8);
        if (IsMirror(current))
        {
            // Ours from an earlier run; Valve's copy is already the backup.
            return;
        }

        if (File.Exists(backup) && Hash(File.ReadAllText(backup, Encoding.UTF8)) == Hash(current))
        {
            return;
        }

        File.Copy(_template, backup, true);
        Log.Change("steam-chord-mirror", $"Valve's chord template backed up as '{Path.GetFileName(backup)}'.");
    }

    private bool RestoreUnderGate(bool forget)
    {
        var backup = _template + BackupSuffix;
        if (!File.Exists(backup))
        {
            return false;
        }

        try
        {
            if (File.Exists(_template) && !IsMirror(File.ReadAllText(_template, Encoding.UTF8)))
            {
                // Valve's file is already in place, from an update or an earlier restore.
            }
            else
            {
                File.Copy(backup, _template, true);
                Log.Change("steam-chord-mirror", "Valve's chord template restored.");
            }

            if (forget)
            {
                File.Delete(backup);
                File.Delete(_template + ResetMarkerSuffix);
            }
            else
            {
                File.WriteAllText(_template + ResetMarkerSuffix, DateTimeOffset.UtcNow.ToString("O"));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Valve's chord template could not be restored: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Rewrites a layout to exactly <paramref name="size" /> UTF-8 bytes: the indentation is
    ///     dropped first, then every separator between quoted tokens if that still does not fit, and
    ///     line breaks pad the rest. Comments are dropped either way.
    /// </summary>
    /// <param name="text">The VDF text.</param>
    /// <param name="size">The byte count Steam's package manifest records for the template.</param>
    /// <returns>The padded text, or null when the layout does not fit even without whitespace.</returns>
    internal static string? FitToSize(string text, int size)
    {
        var tokens = Tokenize(text);
        for (var level = 0; level < 2; level++)
        {
            var compact = Emit(tokens, level);
            var bytes = Encoding.UTF8.GetByteCount(compact);
            if (bytes > size)
            {
                continue;
            }

            StringBuilder padded = new(compact, size);
            padded.Append('\n', size - bytes);
            return padded.ToString();
        }

        return null;
    }

    private static List<(string Text, bool Quoted)> Tokenize(string text)
    {
        List<(string, bool)> tokens = [];
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c is '{' or '}')
            {
                tokens.Add((c.ToString(), false));
                i++;
                continue;
            }

            if (c == '"')
            {
                var start = i;
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    i += text[i] == '\\' ? 2 : 1;
                }

                i = Math.Min(i + 1, text.Length);
                tokens.Add((text[start..i], true));
                continue;
            }

            var wordStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
            {
                i++;
            }

            tokens.Add((text[wordStart..i], false));
        }

        return tokens;
    }

    private static string Emit(List<(string Text, bool Quoted)> tokens, int level)
    {
        StringBuilder builder = new();
        var previous = (Text: "", Quoted: false);
        var keyPending = false;
        foreach (var token in tokens)
        {
            if (builder.Length > 0)
            {
                builder.Append(Separator(previous, token, level, keyPending));
            }

            builder.Append(token.Text);
            var brace = token.Text is "{" or "}";
            keyPending = !brace && !keyPending;
            previous = token;
        }

        return builder.ToString();
    }

    private static string Separator((string Text, bool Quoted) previous, (string Text, bool Quoted) next, int level, bool keyPending)
    {
        // Level 0 keeps one pair per line with a tab between key and value; level 1 keeps a space
        // only where two unquoted tokens would otherwise merge.
        if (level == 1)
        {
            return !previous.Quoted && !next.Quoted && previous.Text is not ("{" or "}") && next.Text is not ("{" or "}") ? " " : "";
        }

        return keyPending && next.Text is not ("{" or "}") ? "\t" : "\n";
    }

    /// <summary>
    ///     Every layout Steam keeps for the chord pseudo-app, newest write first. Steam names the
    ///     autosave <c>controller_neptune.vdf</c> or, once it knows the pad, after the pad's vendor,
    ///     product and serial (<c>28de-1205-&lt;serial&gt;.vdf</c>), so the name cannot pick the file;
    ///     the content does.
    /// </summary>
    private List<string> AutosaveCandidates()
    {
        if (!Directory.Exists(_configsRoot))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(_configsRoot)
                .Select(account => Path.Combine(account, "config", ChordAppId.ToString()))
                .Where(Directory.Exists)
                .SelectMany(app => Directory.EnumerateFiles(app, AutosavePattern))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Guide chord autosaves could not be listed: {ex.Message}");
            return [];
        }
    }

    /// <summary>Whether a file is a Steam Deck guide chord layout, as Steam writes it.</summary>
    /// <param name="text">The VDF text.</param>
    /// <returns>Whether the type and the chord progenitor are both present.</returns>
    internal static bool IsChordLayout(string text)
    {
        return NeptuneType.IsMatch(text) && text.Contains(TemplateFileName, StringComparison.Ordinal);
    }

    /// <summary>Whether a template is one this mirror wrote: an autosave names its progenitor, Valve's file does not.</summary>
    /// <param name="text">The template text.</param>
    /// <returns>Whether the text came from an autosave.</returns>
    internal static bool IsMirror(string text)
    {
        return text.Contains("\"progenitor\"", StringComparison.Ordinal);
    }

    private static string Hash(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
