// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.NvidiaGpu;

internal interface INvProfiles
{
    void Load();
    void Save();
    nint GlobalProfile();
    nint Application(string executable, bool create, string profileName);
    nint FindProfile(string name);
    string ProfileName(nint profile);
    (uint Value, bool Explicit) Get(nint profile, uint setting);
    void Set(nint profile, uint setting, uint value);
    void Inherit(nint profile, uint setting);
}

internal sealed record NvOwnedSetting(
    string Profile,
    uint Setting,
    string Executable,
    string ProfileId,
    uint Original,
    bool OriginalExplicit,
    uint Written,
    bool Pending,
    bool Restoring = false)
{
    internal string Key => Identity(Profile, Setting);

    internal static string Identity(string profile, uint setting)
    {
        return profile.ToUpperInvariant() + "/" + setting.ToString("X8");
    }
}

internal sealed record NvProfileJournal(IReadOnlyList<NvOwnedSetting> Entries);

/// <summary>DRS owns application values. The journal owns only the individual values WSGM changed.</summary>
internal sealed class NvProfiles
{
    private readonly INvProfiles _api;
    private readonly Dictionary<string, NvOwnedSetting> _owned;
    private readonly string _path;
    private long _revision;

    internal NvProfiles(INvProfiles api, string stateDirectory)
    {
        _api = api;
        _path = Path.Combine(stateDirectory, "nvidia-profiles.v1.json");
        _owned = DriverStateFile.Read(_path, new NvProfileJournal([])).Entries.ToDictionary(entry => entry.Key);
    }

    internal ApplicationProfileSyncResult Sync(ApplicationProfileSync sync,
        IReadOnlyDictionary<string, NvSettingControl> controls, CancellationToken token)
    {
        if (sync.Revision <= _revision)
        {
            return new ApplicationProfileSyncResult(0, 0, []);
        }

        _revision = sync.Revision;
        var failures = new List<ApplicationProfileFailure>();
        var requests = new Dictionary<string, List<Request>>(StringComparer.Ordinal);
        _api.Load();
        foreach (var game in sync.Profiles)
        {
            foreach (var executable in game.Executables.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var value in game.Values)
                {
                    token.ThrowIfCancellationRequested();
                    if (!controls.TryGetValue(value.CapabilityId, out var control) ||
                        value.InstanceId != control.Descriptor.InstanceId
                        || control.Descriptor.ProfileScope != CapabilityProfileScope.NativePerApplication ||
                        !control.Accepts(value.Value))
                    {
                        failures.Add(new ApplicationProfileFailure(game.ProfileId, executable, value.CapabilityId,
                            "This NVIDIA setting or value is not supported for application profiles."));
                        continue;
                    }

                    try
                    {
                        var handle = _api.Application(executable, false, OwnedProfile(executable));
                        var name = handle == 0 ? OwnedProfile(executable) : _api.ProfileName(handle);
                        var key = NvOwnedSetting.Identity(name, control.Setting.Id);
                        if (!requests.TryGetValue(key, out var list))
                        {
                            requests[key] = list = [];
                        }

                        list.Add(new Request(game.ProfileId, executable, name, control.Setting.Id,
                            value.CapabilityId, NvSettingControl.Decode(value.Value)));
                    }
                    catch (Exception error) when (error is not OperationCanceledException &&
                                                  error is not DriverFailure { Lost: true })
                    {
                        failures.Add(new ApplicationProfileFailure(game.ProfileId, executable, value.CapabilityId,
                            error.Message));
                    }
                }
            }
        }

        var written = 0;
        var removed = 0;
        foreach (var (key, list) in requests)
        {
            token.ThrowIfCancellationRequested();
            if (list.Select(request => request.Value).Distinct().Skip(1).Any())
            {
                failures.AddRange(list.Select(request => Failure(request,
                    "These games share a native NVIDIA profile but request different values for the same setting.")));
                continue;
            }

            var request = list[0];
            try
            {
                Apply(key, request);
                written += list.Count;
            }
            catch (Exception error) when (error is not OperationCanceledException &&
                                          error is not DriverFailure { Lost: true })
            {
                failures.AddRange(list.Select(item => Failure(item, error.Message)));
                // Discard changes that were buffered but never committed. Never retry a failed Save.
                _api.Load();
            }
        }

        foreach (var entry in _owned.Values.Where(entry => !requests.ContainsKey(entry.Key)).ToArray())
        {
            token.ThrowIfCancellationRequested();
            // An invalid/unavailable desired entry is not permission to remove its prior value.
            if (sync.Profiles.Any(game => game.Executables.Contains(entry.Executable, StringComparer.OrdinalIgnoreCase)
                                          && game.Values.Any(value =>
                                              controls.TryGetValue(value.CapabilityId, out var control)
                                              && control.Setting.Id == entry.Setting)))
            {
                continue;
            }

            try
            {
                _api.Load();
                var handle = _api.FindProfile(entry.Profile);
                if (handle != 0)
                {
                    var current = _api.Get(handle, entry.Setting);
                    var alreadyRestored = current.Explicit == entry.OriginalExplicit
                                          && (!entry.OriginalExplicit || current.Value == entry.Original);
                    if (current.Explicit && current.Value == entry.Written && !alreadyRestored)
                    {
                        if (entry.Restoring)
                        {
                            throw new DriverFailure(
                                "An earlier NVIDIA restoration is unconfirmed; it was not repeated.");
                        }

                        _owned[entry.Key] = entry with { Restoring = true };
                        Persist();
                        if (entry.OriginalExplicit)
                        {
                            _api.Set(handle, entry.Setting, entry.Original);
                        }
                        else
                        {
                            _api.Inherit(handle, entry.Setting);
                        }

                        _api.Save();
                        _api.Load();
                        handle = _api.FindProfile(entry.Profile);
                        var restored = _api.Get(handle, entry.Setting);
                        if (restored.Explicit != entry.OriginalExplicit
                            || (entry.OriginalExplicit && restored.Value != entry.Original))
                        {
                            throw new DriverFailure("The NVIDIA profile restoration was not confirmed.", true);
                        }
                    }
                    // A different current value belongs to an external editor and is preserved.
                }

                _owned.Remove(entry.Key);
                Persist();
                removed++;
            }
            catch (Exception error) when (error is not OperationCanceledException &&
                                          error is not DriverFailure { Lost: true })
            {
                failures.Add(new ApplicationProfileFailure(entry.ProfileId, entry.Executable,
                    "driver." + entry.Setting.ToString("x8"), error.Message));
                _api.Load();
            }
        }

        return new ApplicationProfileSyncResult(written, removed, failures);
    }

    private void Apply(string key, Request request)
    {
        _api.Load();
        var handle = _api.Application(request.Executable, true, request.Profile);
        if (!string.Equals(_api.ProfileName(handle), request.Profile, StringComparison.OrdinalIgnoreCase))
        {
            throw new DriverFailure("The executable's NVIDIA profile changed during synchronization.");
        }

        var current = _api.Get(handle, request.Setting);
        _owned.TryGetValue(key, out var previous);
        if (previous is not null && previous.Written == request.Value && !previous.Restoring)
        {
            if (current.Explicit && current.Value == request.Value)
            {
                _owned[key] = previous with { Pending = false, ProfileId = request.ProfileId };
                Persist();
                return;
            }

            throw new DriverFailure(previous.Pending
                ? "An earlier NVIDIA write is unconfirmed. Change the override or use global before trying again."
                : "An external editor changed this NVIDIA setting. Its value was preserved.");
        }

        var retainOriginal = previous is not null && current.Explicit && current.Value == previous.Written;
        var entry = new NvOwnedSetting(request.Profile, request.Setting, request.Executable, request.ProfileId,
            retainOriginal ? previous!.Original : current.Value,
            retainOriginal ? previous!.OriginalExplicit : current.Explicit, request.Value, true);
        _owned[key] = entry;
        Persist(); // Durable intent precedes the first native mutation and its commit.
        if (current.Explicit && current.Value == request.Value)
        {
            _owned[key] = entry with { Pending = false };
            Persist();
            return;
        }

        _api.Set(handle, request.Setting, request.Value);
        _api.Save();
        _api.Load();
        handle = _api.FindProfile(request.Profile);
        var readback = _api.Get(handle, request.Setting);
        if (!readback.Explicit || readback.Value != request.Value)
        {
            throw new DriverFailure("NVIDIA accepted the profile write but did not confirm its value.", true);
        }

        _owned[key] = entry with { Pending = false };
        Persist();
    }

    private void Persist()
    {
        DriverStateFile.Write(_path, new NvProfileJournal(_owned.Values.OrderBy(entry => entry.Key).ToArray()));
    }

    private static ApplicationProfileFailure Failure(Request request, string detail)
    {
        return new ApplicationProfileFailure(request.ProfileId, request.Executable, request.CapabilityId, detail);
    }

    internal static string OwnedProfile(string executable)
    {
        return "WSGM " + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToLowerInvariant())))[..24];
    }

    private sealed record Request(
        string ProfileId,
        string Executable,
        string Profile,
        uint Setting,
        string CapabilityId,
        uint Value);
}
