using System.Globalization;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.IntelGpu.Graphics;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.IntelGpu.Profiles;

/// <summary>One registry value the driver created for a per-application write.</summary>
/// <param name="KeyPath">The <c>3DKeys</c> key path below HKLM.</param>
/// <param name="Name">The value name, <c>&lt;exe&gt;_&lt;Setting&gt;</c>.</param>
internal sealed record RegistryValueName(string KeyPath, string Name);

/// <summary>One override WSGM stored in the driver, and the registry values that hold it.</summary>
/// <param name="ProfileId">The game profile that last claimed it, for diagnostics.</param>
/// <param name="Executable">The executable file name.</param>
/// <param name="CapabilityId">The capability, or the per-application switch's record id.</param>
/// <param name="InstanceId">The adapter's instance id, or the switch's group key.</param>
/// <param name="Written">What was written, in <see cref="ApplicationProfileSynchronizer.Render" /> form.</param>
/// <param name="Names">The value names that appeared when it was written.</param>
internal sealed record SyncEntry(
    string ProfileId,
    string Executable,
    string CapabilityId,
    string? InstanceId,
    string? Written,
    IReadOnlyList<RegistryValueName> Names)
{
    /// <summary>
    ///     Who owns the names: the executable, the capability and the adapter. The game profile is not part
    ///     of it, so a profile taking over another's executable takes over its names too.
    /// </summary>
    [JsonIgnore]
    public string Key => Identity(Executable, CapabilityId, InstanceId);

    public static string Identity(string executable, string capabilityId, string? instanceId)
    {
        return $"{executable.ToLowerInvariant()}\n{capabilityId}\n{instanceId}";
    }
}

/// <summary>What WSGM wrote, persisted in the plugin's state directory.</summary>
/// <param name="Entries">Every override WSGM stored.</param>
internal sealed record SyncRecord(IReadOnlyList<SyncEntry> Entries)
{
    public static SyncRecord Empty { get; } = new([]);
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

    /// <summary>
    ///     The driver switch that makes an executable use its own values at all, or null when the driver
    ///     has none.
    /// </summary>
    INativeApplicationSwitch? ApplicationSwitch => null;

    /// <summary>Writes one executable's values for this feature.</summary>
    /// <param name="executable">The executable file name.</param>
    /// <param name="values">Each capability's value; fields not listed keep the global value.</param>
    /// <returns>Null on success, or a bounded diagnostic.</returns>
    string? WriteForApplication(string executable, IReadOnlyList<(string CapabilityId, CapabilityValue Value)> values);
}

/// <summary>
///     A per-executable driver switch that has to be on for that executable's stored values to apply.
/// </summary>
/// <remarks>
///     Intel's feature 15 (<c>CTL_3D_FEATURE_GLOBAL_OR_PER_APP</c>). It is written once per executable and
///     adapter while any override for that executable is wanted, recorded like an override, and its
///     recorded registry names are deleted once none is.
/// </remarks>
internal interface INativeApplicationSwitch
{
    /// <summary>The id its record entry carries in place of a capability id.</summary>
    string RecordId { get; }

    /// <summary>Identity of the switch, one per adapter.</summary>
    string GroupKey { get; }

    /// <summary>The <c>3DKeys</c> key paths the driver may store the switch under.</summary>
    IReadOnlyList<string> RegistryKeys { get; }

    /// <summary>Turns the executable's own values on.</summary>
    /// <param name="executable">The executable file name.</param>
    /// <returns>Null on success, or a bounded diagnostic.</returns>
    string? EnableFor(string executable);
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
///     or that another override still holds, is never touched.
///     <para>
///         The record is saved after every write and every removal, so a sync cancelled halfway still
///         remembers the names it created. A write whose recorded value is unchanged and whose recorded
///         names are all still there is confirmed rather than repeated. A record that cannot be read is
///         never replaced: every sync then fails and changes nothing. A record that cannot be saved fails
///         the entries it was recording; the driver write is never repeated.
///     </para>
/// </remarks>
internal sealed class ApplicationProfileSynchronizer
{
    private const string FileName = "application-profiles.v1.json";
    private const string Scope = "profiles";
    private const string What = "per-application record";

    /// <summary>What a switch entry records as written: the switch is only ever turned on.</summary>
    private const string SwitchWritten = "per-application";

    private readonly IntelLog _log;
    private readonly string? _path;
    private readonly IRegistryNode _root;

    private readonly Dictionary<(string Executable, IReadOnlyList<string> Keys), HashSet<RegistryValueName>>
        _snapshots =
            [];

    /// <summary>Why the record could not be read, or null when it was read or is absent.</summary>
    private readonly string? _unreadable;

    /// <summary>
    ///     The newest sync applied in this process. WSGM's revision restarts with WSGM, so it is never
    ///     compared with one from an earlier run.
    /// </summary>
    private long _appliedRevision = long.MinValue;

    public ApplicationProfileSynchronizer(IRegistryNode root, string? stateDirectory, IntelLog log)
    {
        _root = root;
        _path = stateDirectory is null ? null : Path.Combine(stateDirectory, FileName);
        _log = log;
        Record = SyncRecord.Empty;
        if (_path is null)
        {
            return;
        }

        try
        {
            Record = DriverStateFile.Read(_path, SyncRecord.Empty);
        }
        catch (DriverFailure failure)
        {
            // Without the record nothing WSGM wrote could be removed, so it is never replaced and nothing
            // per-application is changed. The global controls keep working.
            _unreadable = failure.Message;
            log.Warn(Scope, $"{failure.Message} Per-application Intel settings are left unchanged.");
        }
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
        if (_unreadable is not null)
        {
            return new ApplicationProfileSyncResult(0, 0,
            [
                .. sync.Profiles.SelectMany(profile => profile.Executables.Select(executable =>
                    new ApplicationProfileFailure(profile.ProfileId, executable, "",
                        $"The Intel per-application record could not be read ({_unreadable}); nothing was changed.")))
            ]);
        }

        // WSGM never repeats a revision, so the same one again has nothing new.
        if (sync.Revision <= _appliedRevision)
        {
            _log.Info(Scope, $"Sync {sync.Revision} is older than the applied {_appliedRevision}; skipped.");
            return new ApplicationProfileSyncResult(0, 0, []);
        }

        _snapshots.Clear();
        List<ApplicationProfileFailure> failures = [];
        Dictionary<string, SyncEntry> entries = new(StringComparer.Ordinal);
        foreach (var entry in Record.Entries)
        {
            entries.TryAdd(entry.Key, entry);
        }

        var groups = Group(sync, resolve, failures);
        HashSet<string> wanted = new(StringComparer.Ordinal);

        // The per-application switch first, once per executable and adapter, so a feature write's
        // bracket never mistakes the switch's registry value for its own.
        Dictionary<(string Executable, string Group), (INativeApplicationSwitch Switch, Wanted First)> switches = [];
        foreach (var ((executable, _), (target, items)) in groups)
        {
            if (target.ApplicationSwitch is { } applicationSwitch)
            {
                switches.TryAdd((executable, applicationSwitch.GroupKey), (applicationSwitch, items[0]));
            }
        }

        foreach (var ((executable, group), (applicationSwitch, first)) in switches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = SyncEntry.Identity(first.Executable, applicationSwitch.RecordId, group);
            wanted.Add(key);
            entries.TryGetValue(key, out var earlier);
            if (Confirmed(applicationSwitch.RegistryKeys, executable, [(earlier, SwitchWritten)]))
            {
                entries[key] = earlier! with { ProfileId = first.ProfileId };
                if (Save(entries) is { } unsaved)
                {
                    failures.Add(new ApplicationProfileFailure(first.ProfileId, first.Executable,
                        applicationSwitch.RecordId, NotRecorded(unsaved)));
                }

                continue;
            }

            var error = Bracketed(applicationSwitch.RegistryKeys, executable,
                () => applicationSwitch.EnableFor(first.Executable), out var appeared);
            if (error is not null)
            {
                failures.Add(new ApplicationProfileFailure(first.ProfileId, first.Executable,
                    applicationSwitch.RecordId, error));
                continue;
            }

            entries[key] = new SyncEntry(first.ProfileId, first.Executable, applicationSwitch.RecordId, group,
                SwitchWritten, Merge(earlier, appeared));
            if (Save(entries) is { } notSaved)
            {
                failures.Add(new ApplicationProfileFailure(first.ProfileId, first.Executable,
                    applicationSwitch.RecordId, NotRecorded(notSaved)));
            }
        }

        var written = 0;
        foreach (var ((executable, _), (target, items)) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claims = new (SyncEntry? Earlier, string Written)[items.Count];
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var key = SyncEntry.Identity(item.Executable, item.Value.CapabilityId, item.Value.InstanceId);
                wanted.Add(key);
                claims[index] = (entries.GetValueOrDefault(key), Render(item.Value.Value));
            }

            string? error = null;
            RegistryValueName[] appeared = [];
            var confirmed = Confirmed(target.RegistryKeys, executable, claims);
            if (!confirmed)
            {
                error = Bracketed(target.RegistryKeys, executable,
                    () => target.WriteForApplication(items[0].Executable,
                        [.. items.Select(item => (item.Value.CapabilityId, item.Value.Value))]),
                    out appeared);
            }

            if (error is not null)
            {
                failures.AddRange(items.Select(item => new ApplicationProfileFailure(item.ProfileId,
                    item.Executable, item.Value.CapabilityId, error)));
                continue;
            }

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var (earlier, value) = claims[index];
                entries[SyncEntry.Identity(item.Executable, item.Value.CapabilityId, item.Value.InstanceId)] =
                    new SyncEntry(item.ProfileId, item.Executable, item.Value.CapabilityId, item.Value.InstanceId,
                        value,
                        confirmed ? earlier!.Names : Merge(earlier, appeared));
            }

            if (Save(entries) is { } unrecorded)
            {
                // Applied, but not owned on disk: reported, and never written again for it.
                failures.AddRange(items.Select(item => new ApplicationProfileFailure(item.ProfileId,
                    item.Executable, item.Value.CapabilityId, NotRecorded(unrecorded))));
                continue;
            }

            written += items.Count;
        }

        var removed = Remove(entries, wanted, failures, cancellationToken);
        _appliedRevision = sync.Revision;
        _log.Info(Scope, $"Sync {sync.Revision}: {written} written, {removed} removed, {failures.Count} refused.");
        return new ApplicationProfileSyncResult(written, removed, failures);
    }

    /// <summary>Whether a name is one the driver can match: a plain ASCII file name.</summary>
    /// <param name="executable">The name.</param>
    /// <returns><see langword="true" /> when it may be written.</returns>
    /// <remarks><c>ctl_3d_feature_getset_t</c> carries the name's length in an <c>int8_t</c>.</remarks>
    internal static bool IsExecutableName(string executable)
    {
        return executable.Length is > 0 and <= sbyte.MaxValue
               && executable.All(character => character is > ' ' and < (char)127
                   and not ('\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'));
    }

    /// <summary>A written value in the form the record keeps.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Its kind and value as plain text.</returns>
    internal static string Render(CapabilityValue value)
    {
        return value.Kind switch
        {
            CapabilityValueKind.Boolean => $"boolean:{value.BooleanValue}",
            CapabilityValueKind.Integer => string.Create(CultureInfo.InvariantCulture, $"integer:{value.IntegerValue}"),
            CapabilityValueKind.Choice => $"choice:{value.ChoiceValue}",
            _ => value.Kind.ToString()
        };
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
            catch (Exception error) when (AdapterClassKey.IsRegistryFailure(error))
            {
                _log.Warn(Scope, $"Listing {path} failed: {IntelLog.Describe(error)}");
            }
        }

        return names;
    }

    /// <summary>Groups the wanted overrides into one driver write per executable and feature.</summary>
    private static Dictionary<(string Executable, string Group), (INativeProfileTarget Target, List<Wanted> Items)>
        Group(
            ApplicationProfileSync sync,
            Func<string, string?, INativeProfileTarget?> resolve,
            List<ApplicationProfileFailure> failures)
    {
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

        return groups;
    }

    private static IReadOnlyList<RegistryValueName> Merge(SyncEntry? earlier, IReadOnlyList<RegistryValueName> appeared)
    {
        return earlier is null ? appeared : [.. earlier.Names.Concat(appeared).Distinct()];
    }

    /// <summary>
    ///     Whether every claim already holds: the recorded value is the one wanted, and the names recorded
    ///     for it are all still in the registry.
    /// </summary>
    private bool Confirmed(
        IReadOnlyList<string> keys,
        string executable,
        IReadOnlyList<(SyncEntry? Earlier, string Written)> claims)
    {
        foreach (var (earlier, value) in claims)
        {
            if (earlier is not { Names.Count: > 0 } || earlier.Written != value)
            {
                return false;
            }
        }

        var present = Before(keys, executable);
        foreach (var (earlier, _) in claims)
        {
            foreach (var name in earlier!.Names)
            {
                if (!present.Contains(name))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Runs one driver write between two listings and reports the names that appeared.</summary>
    /// <param name="keys">The <c>3DKeys</c> key paths.</param>
    /// <param name="executable">The lower-case executable name.</param>
    /// <param name="write">The write; null on success, or a diagnostic.</param>
    /// <param name="appeared">The names the write created.</param>
    /// <returns>The write's diagnostic, or null.</returns>
    /// <remarks>The listing after one write is the listing before the next write for the same executable.</remarks>
    private string? Bracketed(
        IReadOnlyList<string> keys,
        string executable,
        Func<string?> write,
        out RegistryValueName[] appeared)
    {
        var before = Before(keys, executable);
        var error = write();
        if (error is not null)
        {
            // The driver may have stored part of it; the next write lists afresh.
            _snapshots.Remove((executable, keys));
            appeared = [];
            return error;
        }

        var after = Snapshot(keys, executable);
        _snapshots[(executable, keys)] = after;
        appeared = [.. after.Where(name => !before.Contains(name))];
        return null;
    }

    private HashSet<RegistryValueName> Before(IReadOnlyList<string> keys, string executable)
    {
        if (!_snapshots.TryGetValue((executable, keys), out var names))
        {
            names = Snapshot(keys, executable);
            _snapshots[(executable, keys)] = names;
        }

        return names;
    }

    /// <summary>
    ///     Removes what is no longer wanted: only its recorded names, and only those no wanted entry holds.
    ///     A delete that fails stays in the record for the next sync.
    /// </summary>
    private int Remove(
        Dictionary<string, SyncEntry> entries,
        HashSet<string> wanted,
        List<ApplicationProfileFailure> failures,
        CancellationToken cancellationToken)
    {
        HashSet<RegistryValueName> held = [];
        foreach (var (key, entry) in entries)
        {
            if (wanted.Contains(key))
            {
                held.UnionWith(entry.Names);
            }
        }

        var removed = 0;
        foreach (var entry in entries.Values.Where(entry => !wanted.Contains(entry.Key)).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<RegistryValueName> left = [];
            foreach (var name in entry.Names)
            {
                if (!held.Contains(name) && !Delete(name))
                {
                    left.Add(name);
                }
            }

            if (left.Count == 0)
            {
                entries.Remove(entry.Key);
                _log.Info(Scope, $"Removed {entry.CapabilityId} for {entry.Executable} ({entry.ProfileId}).");
                if (Save(entries) is { } unsaved)
                {
                    // The stale entry is harmless: its names are gone and a later delete ignores them.
                    failures.Add(new ApplicationProfileFailure(entry.ProfileId, entry.Executable,
                        entry.CapabilityId,
                        $"Removed from the driver, but WSGM could not update its record ({unsaved})."));
                }
                else
                {
                    removed++;
                }
            }
            else
            {
                failures.Add(new ApplicationProfileFailure(entry.ProfileId, entry.Executable, entry.CapabilityId,
                    "The driver's stored value could not be deleted; WSGM needs elevation."));
                entries[entry.Key] = entry with { Names = left };
                held.UnionWith(left);
                _ = Save(entries);
            }
        }

        return removed;
    }

    private bool Delete(RegistryValueName name)
    {
        try
        {
            using var key = _root.OpenSubKey(name.KeyPath, true);
            key?.DeleteValue(name.Name);
            return true;
        }
        catch (Exception error) when (AdapterClassKey.IsRegistryFailure(error))
        {
            _log.Warn(Scope, $"Deleting {name.Name} failed: {IntelLog.Describe(error)}");
            return false;
        }
    }

    /// <summary>
    ///     Keeps the record in memory, so this session can still remove what it wrote, and saves it.
    /// </summary>
    /// <returns>Null when the file was written or there is no state directory, else why it was not.</returns>
    private string? Save(Dictionary<string, SyncEntry> entries)
    {
        Record = new SyncRecord([.. entries.Values]);
        if (_path is null)
        {
            return null;
        }

        try
        {
            DriverStateFile.Write(_path, Record);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Warn(Scope, $"The {What} could not be saved: {IntelLog.Describe(error)}");
            return error.Message;
        }
    }

    private static string NotRecorded(string reason)
    {
        return $"Applied to the driver, but WSGM could not record it ({reason}); remove it in Intel Graphics "
               + "Software if it stays after a restart.";
    }

    private sealed record Wanted(string ProfileId, string Executable, ApplicationCapabilityValue Value);
}

/// <summary>Finds the <c>3DKeys</c> keys of one Intel adapter in the display adapter class.</summary>
internal static class ThreeDKeysLocator
{
    /// <summary>Lists the candidate key paths for a PCI device id.</summary>
    /// <param name="adapters">The display adapters, enumerated once per session.</param>
    /// <param name="pciDeviceId">The PCI device id.</param>
    /// <returns>
    ///     <c>&lt;class&gt;\NNNN\3DKeys</c> for every Intel adapter subkey whose <c>MatchingDeviceId</c>
    ///     names that device. Two identical adapters give two paths; a write's diff then tells which one
    ///     the driver used.
    /// </returns>
    public static IReadOnlyList<string> Find(IReadOnlyList<AdapterClassEntry> adapters, uint pciDeviceId)
    {
        List<string> paths = [];
        foreach (var adapter in adapters)
        {
            if (AdapterClassKey.Matches(adapter.MatchingDeviceId, pciDeviceId))
            {
                paths.Add($@"{adapter.Path}\3DKeys");
            }
        }

        return paths;
    }
}
