using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsDeviceControl;

namespace WSGM.Setup.Engine;

/// <summary>Explicit opt-ins for this setup run. Presets and quiet installation never enable them.</summary>
internal sealed record DriverCompatibilityChoices
{
    public bool DisableMemoryIntegrity { get; init; }
    public bool DisableDriverBlocklist { get; init; }
    public bool DisableControlFlowGuard { get; init; }
    public bool DefenderExclusions { get; init; }
}

internal sealed class DriverCompatibilityRecovery
{
    public bool OwnsMemoryIntegrity { get; set; }
    public bool? OriginalMemoryIntegrity { get; set; }
    public bool OwnsDriverBlocklist { get; set; }
    public bool? OriginalDriverBlocklist { get; set; }
    public bool OwnsControlFlowGuard { get; set; }
    public ControlFlowGuardSetting OriginalControlFlowGuard { get; set; }
    public List<string> AddedPaths { get; set; } = [];
    public List<string> AddedProcesses { get; set; } = [];
    [JsonIgnore]
    public bool Empty => !OwnsMemoryIntegrity && !OwnsDriverBlocklist && !OwnsControlFlowGuard
                         && AddedPaths.Count == 0 && AddedProcesses.Count == 0;
}

/// <summary>Closed Windows operations; tests supply a backend without changing machine security.</summary>
internal interface IDriverCompatibilitySystem
{
    WindowsProtectionState Read();
    void SetMemoryIntegrity(bool? value);
    void SetDriverBlocklist(bool? value);
    void SetControlFlowGuard(ControlFlowGuardSetting value);
    DefenderExclusionState ReadExclusions();
    void AddPath(string path);
    void AddProcess(string path);
    void RemovePath(string path);
    void RemoveProcess(string path);
}

/// <summary>Setup owns consent and recovery; WindowsDeviceControl owns the Windows primitives.</summary>
internal sealed class DriverCompatibility(string recoveryPath, IDriverCompatibilitySystem? system = null)
{
    private readonly IDriverCompatibilitySystem _system = system ?? new WindowsSystem();
    private readonly List<Action> _undo = [];

    internal WindowsProtectionState Read() => _system.Read();

    /// <summary>Records the original before dispatch. Unknown/failed writes retain recovery ownership.</summary>
    internal bool DisableMemoryIntegrity()
    {
        var current = _system.Read();
        if (current.MemoryIntegrity == false)
        {
            return false;
        }
        if (current.MemoryIntegrityLocked)
        {
            throw new InvalidOperationException("Memory Integrity is firmware-locked. Its setting was left unchanged.");
        }
        var record = Load();
        var wasOwned = record.OwnsMemoryIntegrity;
        if (!record.OwnsMemoryIntegrity)
        {
            record.OriginalMemoryIntegrity = current.MemoryIntegrity;
            record.OwnsMemoryIntegrity = true;
            Save(record);
        }
        _undo.Add(() =>
        {
            if (_system.Read().MemoryIntegrity == false)
            {
                _system.SetMemoryIntegrity(current.MemoryIntegrity);
            }
            if (!wasOwned)
            {
                var retained = Load();
                retained.OwnsMemoryIntegrity = false;
                Save(retained);
            }
        });
        _system.SetMemoryIntegrity(false);
        return true;
    }

    internal bool DisableDriverBlocklist()
    {
        var current = _system.Read();
        if (current.VulnerableDriverBlocklist == false)
        {
            return false;
        }
        var record = Load();
        var wasOwned = record.OwnsDriverBlocklist;
        if (!record.OwnsDriverBlocklist)
        {
            record.OriginalDriverBlocklist = current.VulnerableDriverBlocklist;
            record.OwnsDriverBlocklist = true;
            Save(record);
        }
        _undo.Add(() =>
        {
            if (_system.Read().VulnerableDriverBlocklist == false)
            {
                _system.SetDriverBlocklist(current.VulnerableDriverBlocklist);
            }
            if (!wasOwned)
            {
                var retained = Load();
                retained.OwnsDriverBlocklist = false;
                Save(retained);
            }
        });
        _system.SetDriverBlocklist(false);
        return true;
    }

    internal bool DisableControlFlowGuard()
    {
        var current = _system.Read();
        if (current.ControlFlowGuard == ControlFlowGuardSetting.Disabled)
        {
            return false;
        }
        var record = Load();
        var wasOwned = record.OwnsControlFlowGuard;
        if (!record.OwnsControlFlowGuard)
        {
            record.OriginalControlFlowGuard = current.ControlFlowGuard;
            record.OwnsControlFlowGuard = true;
            Save(record);
        }
        _undo.Add(() =>
        {
            if (_system.Read().ControlFlowGuard == ControlFlowGuardSetting.Disabled)
            {
                _system.SetControlFlowGuard(current.ControlFlowGuard);
            }
            if (!wasOwned)
            {
                var retained = Load();
                retained.OwnsControlFlowGuard = false;
                Save(retained);
            }
        });
        _system.SetControlFlowGuard(ControlFlowGuardSetting.Disabled);
        return true;
    }

    internal void AddDefenderExclusions(string installationRoot)
    {
        var root = Path.GetFullPath(installationRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A drive root cannot be a WSGM exclusion.");
        }
        var current = _system.ReadExclusions();
        var record = Load();
        Add(root, current.Paths, record.AddedPaths, _system.AddPath, process: false);
        Add(Path.Combine(root, "App", "WSGM.exe"), current.Processes, record.AddedProcesses, _system.AddProcess,
            process: true);
        return;

        void Add(string path, IReadOnlyList<string> existing, List<string> owned, Action<string> write, bool process)
        {
            if (existing.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            var wasOwned = owned.Contains(path, StringComparer.OrdinalIgnoreCase);
            if (!wasOwned)
            {
                owned.Add(path);
                Save(record);
            }
            _undo.Add(() =>
            {
                var exclusions = _system.ReadExclusions();
                if ((process ? exclusions.Processes : exclusions.Paths).Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    if (process)
                    {
                        _system.RemoveProcess(path);
                    }
                    else
                    {
                        _system.RemovePath(path);
                    }
                }
                if (!wasOwned)
                {
                    var retained = Load();
                    (process ? retained.AddedProcesses : retained.AddedPaths).Remove(path);
                    Save(retained);
                }
            });
            write(path);
        }
    }

    internal bool RollbackChangesThisRun()
    {
        var complete = true;
        foreach (var undo in _undo.AsEnumerable().Reverse())
        {
            try
            {
                undo();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                complete = false;
                SetupLog.Warn("Protection/exclusion rollback incomplete; recovery retained: " + ex.Message);
            }
        }
        _undo.Clear();
        return complete;
    }

    /// <summary>Restores only owned overrides, preserving later user changes and unrelated Defender entries.</summary>
    internal bool Restore()
    {
        var record = Load();
        var complete = true;
        if (record.OwnsMemoryIntegrity || record.OwnsDriverBlocklist || record.OwnsControlFlowGuard)
        {
            RestoreOne(() =>
            {
                if (!record.OwnsMemoryIntegrity)
                {
                    return;
                }
                if (_system.Read().MemoryIntegrity == false)
                {
                    _system.SetMemoryIntegrity(record.OriginalMemoryIntegrity);
                }
                record.OwnsMemoryIntegrity = false;
            });
            RestoreOne(() =>
            {
                if (!record.OwnsDriverBlocklist)
                {
                    return;
                }
                if (_system.Read().VulnerableDriverBlocklist == false)
                {
                    _system.SetDriverBlocklist(record.OriginalDriverBlocklist);
                }
                record.OwnsDriverBlocklist = false;
            });
            RestoreOne(() =>
            {
                if (!record.OwnsControlFlowGuard)
                {
                    return;
                }
                if (_system.Read().ControlFlowGuard == ControlFlowGuardSetting.Disabled)
                {
                    _system.SetControlFlowGuard(record.OriginalControlFlowGuard);
                }
                record.OwnsControlFlowGuard = false;
            });
        }
        if (record.AddedPaths.Count > 0 || record.AddedProcesses.Count > 0)
        {
            foreach (var path in record.AddedPaths.ToArray())
            {
                RestoreOne(() =>
                {
                    if (_system.ReadExclusions().Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        _system.RemovePath(path);
                    }
                    record.AddedPaths.Remove(path);
                });
            }
            foreach (var path in record.AddedProcesses.ToArray())
            {
                RestoreOne(() =>
                {
                    if (_system.ReadExclusions().Processes.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        _system.RemoveProcess(path);
                    }
                    record.AddedProcesses.Remove(path);
                });
            }
        }
        return complete;

        void RestoreOne(Action restore)
        {
            try
            {
                restore();
                Save(record);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                complete = false;
                SetupLog.Warn("Protection/exclusion restoration incomplete: " + ex.Message);
            }
        }
    }

    private DriverCompatibilityRecovery Load()
    {
        return File.Exists(recoveryPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(recoveryPath), SetupJsonContext.Default.DriverCompatibilityRecovery)
              ?? throw new InvalidDataException("The protection recovery record is empty.")
            : new DriverCompatibilityRecovery();
    }

    private void Save(DriverCompatibilityRecovery record)
    {
        if (record.Empty)
        {
            File.Delete(recoveryPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(recoveryPath)!);
        var staging = recoveryPath + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(record, SetupJsonContext.Default.DriverCompatibilityRecovery));
        File.Move(staging, recoveryPath, overwrite: true);
    }

    private sealed class WindowsSystem : IDriverCompatibilitySystem
    {
        public WindowsProtectionState Read() => WindowsProtectionSettings.Read();
        public void SetMemoryIntegrity(bool? value) => WindowsProtectionSettings.SetMemoryIntegrity(value);
        public void SetDriverBlocklist(bool? value) => WindowsProtectionSettings.SetVulnerableDriverBlocklist(value);
        public void SetControlFlowGuard(ControlFlowGuardSetting value) => WindowsProtectionSettings.SetControlFlowGuard(value);
        public DefenderExclusionState ReadExclusions() => WindowsProtectionSettings.ReadDefenderExclusions();
        public void AddPath(string path) => WindowsProtectionSettings.AddDefenderPath(path);
        public void AddProcess(string path) => WindowsProtectionSettings.AddDefenderProcess(path);
        public void RemovePath(string path) => WindowsProtectionSettings.RemoveDefenderPath(path);
        public void RemoveProcess(string path) => WindowsProtectionSettings.RemoveDefenderProcess(path);
    }
}
