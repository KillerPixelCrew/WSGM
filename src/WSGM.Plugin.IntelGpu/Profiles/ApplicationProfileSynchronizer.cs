using System.Globalization;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.IntelGpu.Profiles;

/// <summary>One registry value the driver created for a per-application write.</summary>
/// <param name="KeyPath">The <c>3DKeys</c> key path below HKLM.</param>
/// <param name="Name">The value name, <c>&lt;exe&gt;_&lt;Setting&gt;</c>.</param>
internal sealed record RegistryValueName(string KeyPath, string Name);

/// <summary>One override WSGM stored in the driver, and the registry values that hold it.</summary>
/// <param name="ProfileId">WSGM's game profile id.</param>
/// <param name="Executable">The executable file name.</param>
/// <param name="CapabilityId">The capability.</param>
/// <param name="InstanceId">The adapter's instance id.</param>
/// <param name="Values">The value names that appeared when it was first written.</param>
internal sealed record SyncEntry(
    string ProfileId,
    string Executable,
    string CapabilityId,
    string? InstanceId,
    IReadOnlyList<RegistryValueName> Values)
{
    /// <summary>The identity of the override, independent of the values that hold it.</summary>
    public string Key => $"{ProfileId}\n{Executable.ToLowerInvariant()}\n{CapabilityId}\n{InstanceId}";
}

/// <summary>What WSGM wrote, persisted in the plugin's state directory.</summary>
/// <param name="Revision">The newest sync applied.</param>
/// <param name="Entries">Every override WSGM stored.</param>
internal sealed record SyncRecord(long Revision, IReadOnlyList<SyncEntry> Entries)
{
    public static SyncRecord Empty { get; } = new(long.MinValue, []);
}

/// <summary>One per-application feature write the plugin can perform.</summary>
internal interface INativeProfileTarget
{
    /// <summary>
    ///     Identity of the driver value one write covers. Controls of one feature structure share it,
    ///     so their overrides for one executable are written together.
    /// </summary>
    string GroupKey { get; }

    /// <summary>The <c>3DKeys</c> key paths the driver may store this adapter's values under.</summary>
    IReadOnlyList<string> RegistryKeys { get; }

    /// <summary>Writes one executable's values for this feature.</summary>
    /// <param name="executable">The executable file name.</param>
    /// <param name="values">Each capability's value; fields not listed keep the global value.</param>
    /// <returns>Null on success, or a bounded diagnostic.</returns>
    string? WriteForApplication(string executable, IReadOnlyList<(string CapabilityId, CapabilityValue Value)> values);
}

/// <summary>
///     Keeps the driver's per-application profiles in line with WSGM's game profiles.
/// </summary>
/// <remarks>
///     IGCL writes a per-application value when <c>ctlGetSet3DFeature</c> carries an application name,
///     and the driver stores it as <c>&lt;exe&gt;_&lt;Setting&gt;</c> under the adapter's <c>3DKeys</c>
///     (observed on 2026-09-29: <c>wsgm-probe.exe_Cmaa</c>, <c>_Emul64bitAtomic</c>,
///     <c>_FrameGeneration</c>). IGCL has no delete. So every write is bracketed by a listing of the
///     value names starting with <c>&lt;exe&gt;_</c>, and the names that appeared are recorded. Removing
///     an override deletes exactly those recorded names and nothing else: a name that was already there,
///     or that another profile still uses, is never touched.
/// </remarks>
internal sealed class ApplicationProfileSynchronizer
{
    private const string FileName = "application-profiles.v1.json";
    private readonly IntelLog _log;
    private readonly string? _path;
    private readonly RegistryKey _root;

    public ApplicationProfileSynchronizer(RegistryKey root, string? stateDirectory, IntelLog log)
    {
        _root = root;
        _path = stateDirectory is null ? null : Path.Combine(stateDirectory, FileName);
        _log = log;
        Record = Load();
    }

    /// <summary>The current record.</summary>
    public SyncRecord Record { get; private set; }

    /// <summary>Applies one complete sync.</summary>
    /// <param name="sync">What WSGM wants.</param>
    /// <param name="resolve">Finds the writer for a capability instance, or null when it has none.</param>
    /// <param name="cancellationToken">Stops between writes.</param>
    /// <returns>What was written, removed and refused.</returns>
    public ApplicationProfileSyncResult Apply(
        ApplicationProfileSync sync,
        Func<string, string?, INativeProfileTarget?> resolve,
        CancellationToken cancellationToken)
    {
        if (sync.Revision < Record.Revision)
        {
            _log.Info("profiles", $"Sync {sync.Revision} is older than the applied {Record.Revision}; skipped.");
            return new ApplicationProfileSyncResult(0, 0, []);
        }

        List<ApplicationProfileFailure> failures = [];
        Dictionary<string, SyncEntry> previous = [];
        foreach (var entry in Record.Entries)
        {
            previous.TryAdd(entry.Key, entry);
        }

        // Group the wanted overrides into one driver write per executable and feature.
        Dictionary<(string Executable, string Group), (INativeProfileTarget Target, List<Wanted> Items)> groups = [];
        HashSet<(string Executable, string Capability, string? Instance)> claimed = [];
        foreach (var profile in sync.Profiles)
        {
            foreach (var executable in profile.Executables)
            {
                if (!IsExecutableName(executable))
                {
                    failures.Add(new ApplicationProfileFailure(profile.ProfileId, executable, "",
                        "The executable name is not a plain ASCII file name the driver can match."));
                    continue;
                }

                foreach (var value in profile.Values)
                {
                    if (resolve(value.CapabilityId, value.InstanceId) is not { } target)
                    {
                        failures.Add(new ApplicationProfileFailure(profile.ProfileId, executable, value.CapabilityId,
                            "Not a per-application capability of this driver."));
                        continue;
                    }

                    if (!claimed.Add((executable.ToLowerInvariant(), value.CapabilityId, value.InstanceId)))
                    {
                        failures.Add(new ApplicationProfileFailure(profile.ProfileId, executable, value.CapabilityId,
                            "Another game profile already sets this for the same executable."));
                        continue;
                    }

                    var key = (executable.ToLowerInvariant(), target.GroupKey);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = (target, []);
                        groups[key] = group;
                    }

                    group.Items.Add(new Wanted(profile.ProfileId, executable, value));
                }
            }
        }

        List<SyncEntry> kept = [];
        HashSet<string> wantedKeys = [];
        var written = 0;
        foreach (var ((executable, _), (target, items)) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = Snapshot(target.RegistryKeys, executable);
            var error = target.WriteForApplication(
                items[0].Executable,
                [.. items.Select(item => (item.Value.CapabilityId, item.Value.Value))]);
            var appeared = error is null
                ? Snapshot(target.RegistryKeys, executable).Except(before).ToArray()
                : [];
            foreach (var item in items)
            {
                var entry = new SyncEntry(item.ProfileId, item.Executable, item.Value.CapabilityId,
                    item.Value.InstanceId, []);
                wantedKeys.Add(entry.Key);
                var names = previous.TryGetValue(entry.Key, out var earlier) ? earlier.Values : [];
                if (error is null)
                {
                    written++;
                    kept.Add(entry with { Values = [.. names.Concat(appeared).Distinct()] });
                }
                else
                {
                    failures.Add(new ApplicationProfileFailure(item.ProfileId, item.Executable,
                        item.Value.CapabilityId, error));
                    if (earlier is not null)
                    {
                        kept.Add(earlier);
                    }
                }
            }
        }

        // Remove what is no longer wanted: only its recorded names, and only those no kept entry holds.
        var stillHeld = kept.SelectMany(entry => entry.Values).ToHashSet();
        var removed = 0;
        foreach (var entry in Record.Entries.Where(entry => !wantedKeys.Contains(entry.Key)))
        {
            List<RegistryValueName> left = [];
            foreach (var name in entry.Values.Where(name => !stillHeld.Contains(name)))
            {
                if (!Delete(name))
                {
                    left.Add(name);
                }
            }

            if (left.Count == 0)
            {
                removed++;
                _log.Info("profiles", $"Removed {entry.CapabilityId} for {entry.Executable} ({entry.ProfileId}).");
                continue;
            }

            failures.Add(new ApplicationProfileFailure(entry.ProfileId, entry.Executable, entry.CapabilityId,
                "The driver's stored value could not be deleted; WSGM needs elevation."));
            kept.Add(entry with { Values = left });
            foreach (var name in left)
            {
                stillHeld.Add(name);
            }
        }

        Record = new SyncRecord(sync.Revision, kept);
        Save();
        _log.Info(
            "profiles",
            $"Sync {sync.Revision}: {written} written, {removed} removed, {failures.Count} refused.");
        return new ApplicationProfileSyncResult(written, removed, failures);
    }

    /// <summary>Whether a name is one the driver can match: a plain ASCII file name.</summary>
    /// <param name="executable">The name.</param>
    /// <returns><see langword="true" /> when it may be written.</returns>
    internal static bool IsExecutableName(string executable)
    {
        return executable.Length is > 0 and <= 120
               && executable.All(character => character is > ' ' and < (char)127
                   and not ('\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'));
    }

    /// <summary>The value names under the given keys that belong to one executable.</summary>
    /// <param name="keys">The <c>3DKeys</c> key paths.</param>
    /// <param name="executable">The executable file name.</param>
    /// <returns>Every matching name, with its key.</returns>
    internal HashSet<RegistryValueName> Snapshot(IReadOnlyList<string> keys, string executable)
    {
        HashSet<RegistryValueName> names = [];
        var prefix = executable + "_";
        foreach (var path in keys)
        {
            try
            {
                using var key = _root.OpenSubKey(path);
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetValueNames())
                {
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(new RegistryValueName(path, name));
                    }
                }
            }
            catch (Exception error) when (IsRegistryFailure(error))
            {
                _log.Warn("profiles", $"Listing {path} failed: {error.Message}");
            }
        }

        return names;
    }

    private bool Delete(RegistryValueName name)
    {
        try
        {
            using var key = _root.OpenSubKey(name.KeyPath, true);
            key?.DeleteValue(name.Name, false);
            return true;
        }
        catch (Exception error) when (IsRegistryFailure(error))
        {
            _log.Warn("profiles", $"Deleting {name.Name} failed: {error.Message}");
            return false;
        }
    }

    private SyncRecord Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return SyncRecord.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<SyncRecord>(File.ReadAllText(_path)) ?? SyncRecord.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Without the record nothing can be removed safely, so nothing will be: an unreadable record
            // only means stale overrides stay until the user clears them in Intel's own software.
            _log.Error("profiles", $"The per-application record could not be read: {error.Message}");
            return SyncRecord.Empty;
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Record));
            File.Move(temporary, _path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Error("profiles", $"The per-application record could not be saved: {error.Message}");
        }
    }

    private static bool IsRegistryFailure(Exception error)
    {
        return error is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException;
    }

    private sealed record Wanted(string ProfileId, string Executable, ApplicationCapabilityValue Value);
}

/// <summary>Finds the <c>3DKeys</c> keys of one Intel adapter in the display adapter class.</summary>
internal static class ThreeDKeysLocator
{
    /// <summary>Lists the candidate key paths for a PCI device id.</summary>
    /// <param name="root">The hive.</param>
    /// <param name="classPath">The display adapter class path.</param>
    /// <param name="pciDeviceId">The PCI device id.</param>
    /// <returns>
    ///     <c>&lt;class&gt;\NNNN\3DKeys</c> for every Intel adapter subkey whose <c>MatchingDeviceId</c>
    ///     names that device. Two identical adapters give two paths; a write's diff then tells which one
    ///     the driver used.
    /// </returns>
    public static IReadOnlyList<string> Find(RegistryKey root, string classPath, uint pciDeviceId)
    {
        List<string> paths = [];
        var device = string.Create(CultureInfo.InvariantCulture, $"ven_8086&dev_{pciDeviceId:x4}");
        try
        {
            using var adapters = root.OpenSubKey(classPath);
            if (adapters is null)
            {
                return paths;
            }

            foreach (var name in adapters.GetSubKeyNames())
            {
                if (name.Length != 4 || !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                try
                {
                    using var adapter = adapters.OpenSubKey(name);
                    if (adapter?.GetValue("MatchingDeviceId") is string matching
                        && matching.Contains(device, StringComparison.OrdinalIgnoreCase))
                    {
                        paths.Add($@"{classPath}\{name}\3DKeys");
                    }
                }
                catch (Exception error) when (error is SecurityException or UnauthorizedAccessException
                                                  or IOException)
                {
                    // An unreadable sibling adapter is not this one.
                }
            }
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            return paths;
        }

        return paths;
    }
}
