using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WSGM.Setup.Engine;

/// <summary>Retains the complete previous file installation until setup commits.</summary>
internal sealed class SetupFileTransaction
{
    private readonly Func<string?>? _executingSetup;
    private readonly string _journalPath;
    private readonly Func<string?>? _readRegistration;
    private readonly Action<string>? _relocatedSetup;
    private readonly Action<string?>? _restoreRegistration;
    private readonly Dictionary<string, (string Path, bool Directory)> _targets;
    private Journal? _journal;

    internal SetupFileTransaction(string root, string machineData, Func<string?>? readRegistration = null,
        Action<string?>? restoreRegistration = null,
        Func<string?>? executingSetup = null, Action<string>? relocatedSetup = null)
    {
        _executingSetup = executingSetup;
        _relocatedSetup = relocatedSetup;
        _readRegistration = readRegistration;
        _restoreRegistration = restoreRegistration;
        root = Path.GetFullPath(root);
        machineData = Path.GetFullPath(machineData);
        _journalPath = Path.Combine(machineData, "setup-transaction.json");
        _targets = new Dictionary<string, (string, bool)>(StringComparer.Ordinal)
        {
            ["App"] = (Path.Combine(root, "App"), true),
            ["Plugins"] = (Path.Combine(root, "Plugins"), true),
            ["Packages"] = (Path.Combine(root, "Setup", "Packages"), true),
            ["Setup"] = (Path.Combine(root, "Setup", "WSGM.Setup.exe"), false),
            ["Bundle"] = (Path.Combine(machineData, "bundle.json"), false)
        };
    }

    internal void Recover()
    {
        if (File.Exists(_journalPath))
        {
            using (var stream = File.OpenRead(_journalPath))
            {
                if (stream.Length > 16 * 1024)
                {
                    throw new InvalidDataException("The setup recovery journal is too large.");
                }

                _journal = JsonSerializer.Deserialize<Journal>(stream)
                           ?? throw new InvalidDataException("The setup recovery journal is empty.");
            }

            if (_journal.Schema != 1 || _journal.Existing is null || _journal.BackedUp is null
                || _journal.Existing.Concat(_journal.BackedUp).Any(name => name is null || !_targets.ContainsKey(name)))
            {
                throw new InvalidDataException("The setup recovery journal is unsupported.");
            }

            if (_journal.Committed || _journal.RolledBack)
            {
                Cleanup();
            }
            else
            {
                RollBack();
            }
        }
        else
        {
            CleanupRetiredSetup();
            var app = _targets["App"].Path;
            if (!Directory.Exists(app) && Directory.Exists(app + ".previous"))
            {
                Directory.Move(app + ".previous", app);
            }
        }
    }

    internal void Begin()
    {
        if (File.Exists(_journalPath))
        {
            throw new InvalidOperationException("The previous setup transaction must be recovered first.");
        }

        foreach (var target in _targets.Values)
        {
            Delete(target.Path + ".previous", target.Directory);
        }

        _journal = new Journal
        {
            RegistrationCaptured = _readRegistration is not null,
            PreviousVersion = _readRegistration?.Invoke(),
            Existing =
            [
                .. _targets.Where(target => Exists(target.Value.Path, target.Value.Directory))
                    .Select(target => target.Key)
            ]
        };
        Save();
        foreach (var name in _journal.Existing.Where(name => name != "App"))
        {
            var target = _targets[name];
            Copy(target.Path, target.Path + ".previous", target.Directory);
            _journal.BackedUp.Add(name);
            Save();
        }
    }

    internal void Commit()
    {
        if (_journal is null)
        {
            return;
        }

        _journal.Committed = true;
        try
        {
            Save();
        }
        catch
        {
            _journal.Committed = false;
            throw;
        }

        try
        {
            Cleanup();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetupLog.Warn($"Installed files committed; backup cleanup remains pending: {ex.Message}");
        }
    }

    internal void RollBack()
    {
        if (_journal is null)
        {
            return;
        }

        if (_journal.Committed || _journal.RolledBack)
        {
            Cleanup();
            return;
        }

        foreach (var (name, target) in _targets)
        {
            var backup = target.Path + ".previous";
            if (!_journal.Existing.Contains(name))
            {
                RetireOrDelete(name, target);
            }
            else if (name == "App" && Directory.Exists(backup))
            {
                Delete(target.Path, true);
                Directory.Move(backup, target.Path);
            }
            else if (_journal.BackedUp.Contains(name))
            {
                if (!Exists(backup, target.Directory))
                {
                    throw new IOException($"The previous {name} backup is missing; recovery was stopped.");
                }

                if (!target.Directory && File.Exists(target.Path)
                                      && File.ReadAllBytes(target.Path).AsSpan()
                                          .SequenceEqual(File.ReadAllBytes(backup)))
                {
                    continue;
                }

                RetireOrDelete(name, target);
                Copy(backup, target.Path, target.Directory);
            }
        }

        if (_journal.RegistrationCaptured)
        {
            if (_restoreRegistration is null)
            {
                throw new InvalidOperationException("Setup registration recovery is unavailable.");
            }

            _restoreRegistration(_journal.PreviousVersion);
        }

        _journal.RolledBack = true;
        Save();
        Cleanup();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        var temporary = _journalPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, _journal);
            stream.Flush(true);
        }

        File.Move(temporary, _journalPath, true);
    }

    private void Cleanup()
    {
        foreach (var target in _targets.Values)
        {
            Delete(target.Path + ".previous", target.Directory);
        }

        File.Delete(_journalPath);
        _journal = null;
        CleanupRetiredSetup();
    }

    private void RetireOrDelete(string name, (string Path, bool Directory) target)
    {
        if (name == "Setup" && File.Exists(target.Path)
                            && _executingSetup?.Invoke() is { } executing
                            && string.Equals(Path.GetFullPath(executing), target.Path,
                                StringComparison.OrdinalIgnoreCase))
        {
            if (_relocatedSetup is null)
            {
                throw new InvalidOperationException("Executing setup recovery has no image owner.");
            }

            // Windows can rename an executing image but cannot delete/overwrite it. Keep
            // that exact image alive under a private name, then restore the advertised path.
            var retained = target.Path + ".wsgm-retired-" + Guid.NewGuid().ToString("N");
            File.Move(target.Path, retained);
            _relocatedSetup(retained);
        }
        else
        {
            Delete(target.Path, target.Directory);
        }
    }

    private void CleanupRetiredSetup()
    {
        var setup = _targets["Setup"].Path;
        var folder = Path.GetDirectoryName(setup)!;
        if (!Directory.Exists(folder))
        {
            return;
        }

        var prefix = Path.GetFileName(setup) + ".wsgm-retired-";
        foreach (var path in Directory.EnumerateFiles(folder, prefix + "*"))
        {
            if (!Guid.TryParseExact(Path.GetFileName(path)[prefix.Length..], "N", out _))
            {
                continue;
            }

            if (_executingSetup?.Invoke() is { } executing
                && string.Equals(Path.GetFullPath(executing), path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The current setup can still be executing from this retained image. A later
                // setup cleans it after process exit; it is not an incomplete installation.
            }
        }
    }

    private static bool Exists(string path, bool directory)
    {
        return directory ? Directory.Exists(path) : File.Exists(path);
    }

    private static void Delete(string path, bool directory)
    {
        if (Exists(path, directory))
        {
            if (directory)
            {
                Directory.Delete(path, true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    private static void Copy(string source, string destination, bool directory)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Setup backups cannot follow a reparse point.");
        }

        if (!directory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            Copy(entry, Path.Combine(destination, Path.GetFileName(entry)),
                (attributes & FileAttributes.Directory) != 0);
        }
    }

    internal sealed class Journal
    {
        public int Schema { get; set; } = 1;
        public bool Committed { get; set; }
        public bool RolledBack { get; set; }
        public bool RegistrationCaptured { get; set; }
        public string? PreviousVersion { get; set; }
        public List<string> Existing { get; set; } = [];
        public List<string> BackedUp { get; set; } = [];
    }
}
