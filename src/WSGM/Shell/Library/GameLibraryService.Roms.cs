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
    /// <inheritdoc />
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
            if (single && copy.SystemId == "auto")
            {
                return Refuse("Choose a system for a single ROM file.");
            }
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
                var defaults = GameLibraryEmulators.RomDefaults(ReadSettings(), content);
                var selected = content.Copy();
                selected.EmulatorInstallationId =
                    installationId.Length == 0 ? defaults.Installation : installationId;
                selected.CoreId = installationId.Length == 0 ? defaults.Core : coreId;
                selected.FollowSystemPreference = selected.EmulatorInstallationId.Length == 0;
                var store = _emulators.ReadStore();
                if (installationId.Length > 0)
                {
                    var receipt = store.Installations.FirstOrDefault(value => value.Id == installationId)
                                  ?? throw new InvalidOperationException("Choose an installed emulator.");
                    _ = EmulatorStorage.ValidateSelection(receipt, content.SystemId, coreId);
                }

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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
            var defaults = GameLibraryEmulators.RomDefaults(ReadSettings(), content);
            content.Arguments = arguments.Count > 0 ? [.. arguments] : [.. defaults.Arguments];
            entry.ContentChanged = true;
            entry.Selected = true;
            return null;
        });
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IReadOnlyList<RomSystemProfile> ReadRomSystems()
    {
        var (_, systems) = _emulators.Read();
        return [RomProfiles.Automatic, .. systems];
    }

    /// <inheritdoc />
    public RomEmulatorState ReadRomState()
    {
        return _emulators.Read().State;
    }

    private Task<SteamUiCommandResult> SaveSources(Action<GameLibraryConfig> change)
    {
        return Task.FromResult(ChangeSettings(change, _scanner.ScanOrQueue, settled: UpdateEmulatorDependencies));
    }

    private static string? RomArgumentsRefusal(IReadOnlyList<string> arguments)
    {
        return arguments.Any(argument => argument.Contains('\0'))
               || (arguments.Count > 0 &&
                   !arguments.Any(argument => argument.Contains("{rom}", StringComparison.Ordinal)))
            ? "Arguments must contain {rom} and cannot contain a NUL character."
            : null;
    }
}
