using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed partial class GameLibraryService
{
    public Task<SteamUiCommandResult> AddRomSourceAsync(RomSourceConfig source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_updateSettings is null)
        {
            return Refuse("ROM sources cannot be configured in this session.");
        }

        try
        {
            var copy = source.Copy();
            copy.SystemId = EmulatorStorage.NormalizeSystemId(copy.SystemId);
            copy.Extensions = GameLibraryRules.NormalizeExtensions(copy.Extensions);
            if (RomArgumentsRefusal(copy.Arguments) is { } argumentRefusal)
            {
                return Refuse(argumentRefusal);
            }

            if (!ReadRomSystems().Any(profile => profile.Id == copy.SystemId))
            {
                return Refuse("Choose a supported ROM system or an installed core's system.");
            }

            copy.Root.AbsolutePath = Path.GetFullPath(copy.Root.AbsolutePath);
            var single = !copy.Root.Directory || File.Exists(copy.Root.AbsolutePath);
            if (copy.Root.VolumeId.Length == 0)
            {
                copy.Root = ManagedContentStorage.CapturePath(copy.Root.AbsolutePath, !single);
            }

            copy.Root.Directory = !single;
            if (copy.Name.Trim().Length == 0)
            {
                copy.Name = single
                    ? Path.GetFileNameWithoutExtension(copy.Root.AbsolutePath)
                    : Path.GetFileName(copy.Root.AbsolutePath.TrimEnd('\\', '/'));
                if (copy.Name.Length == 0)
                {
                    copy.Name = copy.Root.AbsolutePath;
                }
            }

            if (copy.Id.Length == 0)
            {
                copy.Id = Guid.NewGuid().ToString("N");
            }

            if (single)
            {
                ManualShortcutConfig entry = new()
                {
                    Id = copy.Id, Name = copy.Name, Location = copy.Name, RomPath = copy.Root,
                    SystemId = copy.SystemId, EmulatorInstallationId = copy.EmulatorInstallationId,
                    CoreId = copy.CoreId, RomArguments = [.. copy.Arguments], RomExtensions = [.. copy.Extensions]
                };
                return SaveSources(settings =>
                {
                    settings.ManualSources.RemoveAll(existing => existing.Id == entry.Id);
                    settings.ManualSources.Add(entry);
                });
            }

            if (ReadSettings().RomSources.Any(existing => existing.Id != copy.Id && existing.SystemId == copy.SystemId
                    && ManagedContentStorage.SameLocation(existing.Root, copy.Root)))
            {
                return Refuse("That ROM source is already configured. Edit the existing source instead.");
            }

            return SaveSources(settings =>
            {
                settings.RomSources.RemoveAll(existing => existing.Id == copy.Id);
                settings.RomSources.Add(copy);
            });
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return Refuse(ex.Message);
        }
    }

    public Task<SteamUiCommandResult> RemoveRomSourceAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (Guard() is { } blocked)
            {
                return Task.FromResult(blocked);
            }
        }

        return SaveSources(settings =>
        {
            settings.RomSources.RemoveAll(source => source.Id == id);
            settings.DisabledSources.RemoveAll(source => source == id);
        });
    }

    public Task<SteamUiCommandResult> AddManualSourceAsync(ManualShortcutConfig source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var copy = source.Copy();
            copy.Target = Path.GetFullPath(copy.Target);
            if (string.IsNullOrWhiteSpace(copy.Name))
            {
                return Refuse("Give the shortcut a name.");
            }

            if (copy.Id.Length == 0)
            {
                copy.Id = Guid.NewGuid().ToString("N");
            }


            return SaveSources(settings =>
            {
                settings.ManualSources.RemoveAll(existing => existing.Id == copy.Id);
                settings.ManualSources.Add(copy);
            });
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return Refuse(ex.Message);
        }
    }

    public Task<SteamUiCommandResult> SetRomEmulatorAsync(string id, string installationId, string coreId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditEntry(id, true, entry =>
        {
            if (entry.Game.Content is not { SourceKind: LibrarySourceKind.Rom } content)
            {
                return "This title is not a ROM import.";
            }

            try
            {
                var defaults = RomDefaults(content);
                var selected = content.Copy();
                selected.EmulatorInstallationId =
                    installationId.Length == 0 ? defaults.Installation : installationId;
                selected.CoreId = installationId.Length == 0 ? defaults.Core : coreId;
                selected.FollowSystemPreference = selected.EmulatorInstallationId.Length == 0;
                var store = ReadEmulatorStore();
                if (installationId.Length > 0)
                {
                    var receipt = store.Installations.FirstOrDefault(value => value.Id == installationId)
                                  ?? throw new InvalidOperationException("Choose an installed emulator.");
                    _ = EmulatorStorage.ValidateSelection(receipt, content.SystemId, coreId);
                }

                var check = ManagedContentStorage.Check(selected, store);
                content.Availability = check.Availability;
                content.AvailabilityDetail = check.Detail;

                content.EmulatorInstallationId = selected.EmulatorInstallationId;
                content.CoreId = selected.CoreId;
                content.FollowSystemPreference = selected.FollowSystemPreference;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                                           or InvalidOperationException)
            {
                return ex.Message;
            }

            entry.EmulatorOverride = installationId;
            entry.CoreOverride = coreId;
            entry.ContentChanged = true;
            entry.Selected = true;
            return null;
        });
    }

    public Task<SteamUiCommandResult> RemoveManualSourceAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (Guard() is { } blocked)
            {
                return Task.FromResult(blocked);
            }
        }

        return SaveSources(settings =>
        {
            settings.ManualSources.RemoveAll(source => source.Id == id);
            settings.DisabledSources.RemoveAll(source => source == id);
        });
    }

    public Task<SteamUiCommandResult> SetRomTitleAsync(string id, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditEntry(id, true, entry =>
        {
            if (entry.Game.Content is not { SourceKind: LibrarySourceKind.Rom } content ||
                string.IsNullOrWhiteSpace(name))
            {
                return "Choose a ROM title and give it a name.";
            }

            entry.TitleName = name.Trim();
            content.Name = entry.TitleName;
            entry.Game = entry.Game with { Name = entry.TitleName };
            entry.Plan = entry.Plan with { Name = entry.TitleName };
            entry.ContentChanged = true;
            entry.Selected = true;
            return null;
        });
    }

    public Task<SteamUiCommandResult> SetRomArgumentsAsync(string id, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditEntry(id, true, entry =>
        {
            if (entry.Game.Content is not { SourceKind: LibrarySourceKind.Rom } content)
            {
                return "Choose a ROM title.";
            }

            if (RomArgumentsRefusal(arguments) is { } refusal)
            {
                return refusal;
            }

            entry.ArgumentsOverride = [.. arguments];
            var defaults = RomDefaults(content);
            content.Arguments = arguments.Count > 0 ? [.. arguments] : [.. defaults.Arguments];
            entry.ContentChanged = true;
            entry.Selected = true;
            return null;
        });
    }

    public Task<SteamUiCommandResult> SetCleanupAsync(string id, bool cleanup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EditEntry(id, false, entry =>
        {
            if (entry.Game.Content is null || entry.AppId == 0 || entry.Plan.Action == ImportAction.Conflict)
            {
                return "Only an unchanged imported managed shortcut can be selected for cleanup.";
            }

            entry.Cleanup = cleanup;
            if (cleanup)
            {
                entry.BeforeCleanup ??= entry.Plan;
                entry.Plan = entry.Plan with
                {
                    Action = ImportAction.Remove, Selectable = true,
                    Reason = "Apply removes this shortcut and keeps the content and saves."
                };
            }
            else if (entry.BeforeCleanup is { } original)
            {
                entry.Plan = original;
                entry.BeforeCleanup = null;
            }

            entry.Selected = entry.Selectable && (cleanup || entry.PendingChange || entry.Plan.Preselect);
            return null;
        }, true);
    }

    public async Task<SteamUiCommandResult> RecheckAvailabilityAsync(string id, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;
        try
        {
            var observation = await Task.Run(() =>
            {
                Dictionary<string, ManagedContentCheck> result = [];
                Dictionary<string, ManagedContentRecord> expected = [];
                var emulatorStore = ReadEmulatorStore();
                var context = new ManagedContentCheckContext();
                foreach (var record in _store.Entries().Where(record => record.Content is not null
                                                                        && (id.Length == 0 || record.Content.Id == id ||
                                                                            EntryId(record.Source, record.Key) == id)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var content = record.Content!;
                    expected[content.Id] = content;
                    result[content.Id] = ManagedContentStorage.Check(content, emulatorStore, context);
                }

                return (Checks: result, Expected: expected);
            }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            ApplyAvailability(observation.Checks, observation.Expected, cancellationToken);
            return SteamUiCommandResult.Applied;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ImportStateException)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }
    }

    public IReadOnlyList<RomSystemProfile> ReadRomSystems()
    {
        _ = ReadRomState();
        return _romSystems ?? RomProfiles.All;
    }

    public RomEmulatorState ReadRomState()
    {
        lock (_gate)
        {
            _romEmulators ??= _emulators?.ReadRomState() ?? RomEmulatorState.Empty;
            _romSystems ??= _emulators?.ReadPageState().RomSystems ?? RomProfiles.All;
            return _romEmulators;
        }
    }

    private (string Installation, string Core, IReadOnlyList<string> Arguments) RomDefaults(
        ManagedContentRecord content)
    {
        var settings = ReadSettings();
        if (content.SourceId == "manual")
        {
            var manual = settings.ManualSources.FirstOrDefault(source => source.Id == content.SourceKey);
            return (manual?.EmulatorInstallationId ?? "", manual?.CoreId ?? "", manual?.RomArguments ?? []);
        }

        var library = settings.RomSources.FirstOrDefault(source => source.Id == content.SourceId);
        return (library?.EmulatorInstallationId ?? "", library?.CoreId ?? "", library?.Arguments ?? []);
    }

    private static IEnumerable<string> ContentNotes(ManagedContentRecord? content)
    {
        if (content is null)
        {
            yield break;
        }

        yield return "Location: " + content.Location;
        yield return "Content: " + content.BackingPath.AbsolutePath;
        yield return content.AvailabilityDetail;
        foreach (var companion in content.RequiredPaths)
        {
            yield return "Required companion: " + companion.AbsolutePath;
        }
    }

    private Task<SteamUiCommandResult> SaveSources(Action<GameLibraryConfig> change)
    {
        lock (_gate)
        {
            if (Guard() is { } blocked)
            {
                return Task.FromResult(blocked);
            }
        }

        if (!TryUpdateSettings(change, out var refusal))
        {
            return Task.FromResult(refusal);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(ShuttingDown);
            }

            ScanOrQueue();
        }

        UpdateEmulatorDependencies();
        Notify();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private static string? RomArgumentsRefusal(IReadOnlyList<string> arguments)
    {
        return arguments.Any(argument => argument.Contains('\0'))
               || (arguments.Count > 0 &&
                   !arguments.Any(argument => argument.Contains("{rom}", StringComparison.Ordinal)))
            ? "Arguments must contain {rom} and cannot contain a NUL character."
            : null;
    }

    private void ApplyAvailability(IReadOnlyDictionary<string, ManagedContentCheck> checks,
        IReadOnlyDictionary<string, ManagedContentRecord> expected, CancellationToken cancellationToken)
    {
        var changed = false;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return;
            }

            _store.UpdateAvailability(checks, expected);

            foreach (var entry in _entries.Values)
            {
                if (entry.Game.Content is not { } content || !checks.TryGetValue(content.Id, out var check)
                                                          || !expected.TryGetValue(content.Id, out var checkedContent)
                                                          || !ManagedContentStorage.SameLaunch(content, checkedContent)
                                                          || (content.Availability == check.Availability &&
                                                              content.AvailabilityDetail == check.Detail))
                {
                    continue;
                }

                content.Availability = check.Availability;
                content.AvailabilityDetail = check.Detail;
                changed = true;
            }

            if (changed)
            {
                Publish();
            }
        }

        if (changed)
        {
            Notify();
        }

        LibraryBadges.UpdateShortcuts(ReadManagedEntries().Where(entry => entry.AppId > 0)
            .Select(entry => new SteamShortcutAvailability(entry.AppId,
                entry.Content.Availability == ManagedContentAvailability.Available,
                entry.Content.Location, entry.Content.AvailabilityDetail)).ToArray());
    }

    private EmulatorStore ReadEmulatorStore()
    {
        return _emulators?.ReadStore() ?? new EmulatorStore();
    }

    private void OnEmulatorsChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _romEmulators = null;
            _romSystems = null;
            _sourceCache = null;
            Publish();
        }

        UpdateEmulatorDependencies();
        Notify();
    }

    internal Task<SteamUiCommandResult> RecheckShortcutAsync(uint appId, CancellationToken cancellationToken)
    {
        var content = ReadManagedEntries().FirstOrDefault(entry => entry.AppId == appId)?.Content;
        return content is null
            ? Refuse("This shortcut has no tracked content.")
            : RecheckAvailabilityAsync(content.Id, cancellationToken);
    }

    private void NotifyManagedEntriesChanged()
    {
        UpdateEmulatorDependencies();
        ManagedEntriesChanged?.Invoke();
    }

    private void UpdateEmulatorDependencies()
    {
        if (_emulators is null)
        {
            return;
        }

        var store = ReadEmulatorStore();
        var settings = ReadSettings();

        string Selected(string systemId, string installationId)
        {
            return installationId.Length > 0
                ? installationId
                : ManagedContentStorage.PreferredSystem(store, systemId)?.InstallationId ?? "";
        }

        var sources = settings.RomSources.Select(source => Selected(source.SystemId, source.EmulatorInstallationId))
            .Concat(settings.ManualSources.Where(source => source.RomPath is not null)
                .Select(source => Selected(source.SystemId, source.EmulatorInstallationId)))
            .GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var titles = _store.CountEmulatorDependencies(store);

        _emulators.UpdateDependencies(store.Installations.Select(installation => new EmulatorDependencyCount(
            installation.Id, sources.GetValueOrDefault(installation.Id),
            titles?.GetValueOrDefault(installation.Id))).ToArray());
    }
}
