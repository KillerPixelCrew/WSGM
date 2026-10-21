using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Reconciles emulator UI shortcuts through the importer's writer and durable ownership records.</summary>
internal sealed class EmulatorShortcutSync : IDisposable
{
    internal const string Source = "wsgm-emulators";
    private readonly EmulatorManager _manager;
    private readonly ImportStateStore _store;
    private readonly Func<SteamShortcutWriter?> _writer;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> _readLibrary;
    private readonly Func<uint, CancellationToken, Task<ExistingShortcut?>> _readShortcut;
    private readonly Func<bool> _inputManaged;
    private readonly Func<bool> _ready;
    private readonly Func<EmulatorDefinition, uint, CancellationToken, Task<int>> _artwork;
    private readonly Action<string> _status;
    private readonly Action _changed;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(initialCount: 0, maxCount: 1);
    private readonly Lock _gate = new();
    private bool _disposed;

    internal EmulatorShortcutSync(EmulatorManager manager, ImportStateStore store,
        Func<SteamShortcutWriter?> writer,
        Func<CancellationToken, Task<IReadOnlyList<ExistingShortcut>>> readLibrary,
        Func<uint, CancellationToken, Task<ExistingShortcut?>> readShortcut,
        Func<bool> inputManaged, Func<bool> ready,
        Func<EmulatorDefinition, uint, CancellationToken, Task<int>> artwork, Action<string> status, Action changed)
    {
        _manager = manager;
        _store = store;
        _writer = writer;
        _readLibrary = readLibrary;
        _readShortcut = readShortcut;
        _inputManaged = inputManaged;
        _ready = ready;
        _artwork = artwork;
        _status = status;
        _changed = changed;
        manager.InstallationsChanged += Request;
        Completion = Task.Run(RunAsync);
        Request();
    }

    internal Task Completion { get; }

    internal void Request()
    {
        lock (_gate)
        {
            if (!_disposed && _wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
    }

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

        _manager.InstallationsChanged -= Request;
        _stop.Cancel();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_stop.Token).ConfigureAwait(false);
                if (!_manager.Initialized || !_ready())
                {
                    _status("Steam shortcuts will sync when Steam is ready.");
                    continue;
                }

                try
                {
                    await ReconcileAsync(_stop.Token).ConfigureAwait(false);
                    _status("");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
                {
                    _status(ex.Message);
                    Log.Warn("Emulator Steam shortcuts: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            _wake.Dispose();
            _stop.Dispose();
        }
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        var snapshot = _manager.GetSnapshot();
        var writer = _writer() ?? throw new InvalidOperationException("Steam is unavailable. Shortcut changes are pending.");
        var library = await _readLibrary(token).ConfigureAwait(false);
        var records = _store.Entries().Where(item => item.Source == Source).ToArray();
        var artworkErrors = new List<string>();
        var wanted = snapshot.Installations.Where(item => item.SteamShortcutEnabled).ToDictionary(item => item.Id);
        foreach (var recorded in records.Where(item => !wanted.ContainsKey(item.Key)))
        {
            var live = ResolveRecorded(recorded, library);
            if (live is not null)
            {
                live = await _readShortcut(live.AppId, token).ConfigureAwait(false);
                if (live is not null)
                {
                    RequireOwned(recorded, live);
                    var removed = await writer.RemoveAsync(live.AppId, token).ConfigureAwait(false);
                    if (!removed.Confirmed)
                    {
                        throw new InvalidOperationException(removed.Error);
                    }
                }
            }

            // An uncertain creation is retained until a full read locates its exact command.
            if (recorded.AppId == 0 && live is null)
            {
                throw new InvalidOperationException("An emulator shortcut creation is unconfirmed. Check Steam before retrying.");
            }

            _store.Forget(Source, recorded.Key);
            _changed();
        }

        if (wanted.Count == 0)
        {
            return;
        }

        var launcher = PackagedLauncherShortcut.ResolveLauncher()
                       ?? throw new InvalidOperationException("The WSGM content launcher is missing.");
        foreach (var installed in wanted.Values)
        {
            token.ThrowIfCancellationRequested();
            var content = new ManagedContentRecord
            {
                Id = ManagedContentStorage.ContentId(Source, installed.Id), SourceId = Source,
                SourceKey = installed.Id, SourceKind = LibrarySourceKind.Emulator, Name = installed.Name,
                Location = "Emulator Manager", EmulatorInstallationId = installed.Id
            };
            var route = new ShortcutRoute("managed", "Emulator interface", "", "", "",
                "Resolves the current emulator installation at launch.", ManagedId: content.Id);
            if (!CommandShortcut.TryCompose(route, launcher, out var fields, out var refusal))
            {
                throw new InvalidOperationException(refusal);
            }

            if (installed.SteamShortcutLaunchMode is < 0 or > 3)
            {
                throw new InvalidOperationException($"{installed.Name}: choose a supported launch mode in Emulator Manager.");
            }

            var mode = LaunchWrapperCommand.ForCurrentInputMode((LaunchWrapperMode)installed.SteamShortcutLaunchMode,
                _inputManaged());
            if (mode != LaunchWrapperMode.None)
            {
                var helper = LaunchWrapperCommand.HelperPathForCurrentDeployment();
                if (!File.Exists(helper))
                {
                    throw new FileNotFoundException("The WSGM input-lease launcher is missing.", helper);
                }

                fields = fields with
                {
                    Target = LaunchWrapperCommand.ShortcutTarget(helper),
                    LaunchOptions = LaunchWrapperCommand.ShortcutArguments(mode, fields.Target, fields.LaunchOptions)
                };
            }

            var recorded = records.SingleOrDefault(item => item.Key == installed.Id);
            var live = recorded is null ? ResolveRecorded(new ImportedEntry
            {
                Target = fields.Target, StartDirectory = fields.StartDirectory, LaunchOptions = fields.LaunchOptions
            }, library) : ResolveRecorded(recorded, library);
            if (recorded is not null && live is not null)
            {
                RequireOwned(recorded, live);
            }

            if (recorded is { AppId: 0 } && live is null)
            {
                throw new InvalidOperationException($"{installed.Name}: shortcut creation is unconfirmed. Check Steam before retrying.");
            }

            var entry = recorded?.Copy() ?? new ImportedEntry
            {
                Source = Source, Key = installed.Id, Name = installed.Name, Route = "managed",
                Mode = nameof(ImportMode.SteamIntegration),
                Target = fields.Target, StartDirectory = fields.StartDirectory, LaunchOptions = fields.LaunchOptions,
                ImportedUtc = DateTime.UtcNow.ToString("O")
            };
            entry.Content = content;
            if (live is null)
            {
                entry.AppId = 0;
                entry.Target = fields.Target;
                entry.StartDirectory = fields.StartDirectory;
                entry.LaunchOptions = fields.LaunchOptions;
                entry.ConfirmedUtc = "";
                // Persist before dispatch: a lost answer or process crash must never cause a duplicate add.
                token.ThrowIfCancellationRequested();
                _store.Save(entry);
                // Saving admits this add. Resolve its outcome even if shutdown starts now, so an
                // unsent cancellation cannot strand the durable record as an uncertain creation.
                var added = await writer.AddAsync(installed.Name, fields, CancellationToken.None).ConfigureAwait(false);
                if (!added.Confirmed && !added.CreationMayExist)
                {
                    _store.Forget(Source, installed.Id);
                    throw new InvalidOperationException(added.Error ?? "Steam did not create the emulator shortcut.");
                }

                entry.AppId = added.AppId;
                entry.ConfirmedUtc = added.Confirmed ? DateTime.UtcNow.ToString("O") : "";
                _store.Save(entry);
                if (!added.Confirmed)
                {
                    throw new InvalidOperationException(added.Error ?? "Steam did not confirm the emulator shortcut.");
                }

                if (added.Mismatch is { } mismatch)
                {
                    throw new InvalidOperationException(installed.Name + ": " + mismatch);
                }
            }
            else
            {
                var fresh = await _readShortcut(live.AppId, token).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("The emulator shortcut disappeared. Sync again.");
                RequireOwned(entry, fresh);
                entry.AppId = fresh.AppId;
                entry.ConfirmedUtc = DateTime.UtcNow.ToString("O");
                _store.Save(entry);
                if (!CommandShortcut.Same(fresh, fields.Target, fields.LaunchOptions)
                    || !CommandShortcut.SameFolder(fresh.StartDirectory, fields.StartDirectory))
                {
                    entry.PendingLaunch = fields;
                    _store.Save(entry);
                    var updated = await writer.UpdateAsync(fresh.AppId, fields, token).ConfigureAwait(false);
                    if (!updated.Confirmed)
                    {
                        throw new InvalidOperationException(updated.Error);
                    }
                }

                entry.Target = fields.Target;
                entry.StartDirectory = fields.StartDirectory;
                entry.LaunchOptions = fields.LaunchOptions;
                entry.PendingLaunch = null;
                _store.Save(entry);
            }

            _changed();
            if (entry.ArtworkApplied == 0 && snapshot.Definitions.FirstOrDefault(item => item.Id == installed.DefinitionId)
                is { } definition)
            {
                try
                {
                    entry.ArtworkApplied = await _artwork(definition, entry.AppId, token).ConfigureAwait(false);
                    _store.Save(entry);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
                {
                    artworkErrors.Add(installed.Name + ": " + ex.Message);
                }
            }
        }

        if (artworkErrors.Count > 0)
        {
            throw new InvalidOperationException("Steam shortcuts synced; artwork: " + string.Join(" ", artworkErrors));
        }
    }

    private static ExistingShortcut? ResolveRecorded(ImportedEntry entry, IReadOnlyList<ExistingShortcut> library)
    {
        if (entry.AppId > 0)
        {
            return library.SingleOrDefault(item => item.AppId == entry.AppId);
        }

        var matches = library.Where(item => CommandShortcut.Same(item, entry.Target, entry.LaunchOptions)
                                             && CommandShortcut.SameFolder(item.StartDirectory, entry.StartDirectory))
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException("Several emulator shortcuts match an unconfirmed write. Check Steam before retrying.")
        };
    }

    private void RequireOwned(ImportedEntry entry, ExistingShortcut live)
    {
        if (entry.PendingLaunch is { } pending)
        {
            if (CommandShortcut.Same(live, pending.Target, pending.LaunchOptions)
                && CommandShortcut.SameFolder(live.StartDirectory, pending.StartDirectory))
            {
                entry.Target = pending.Target;
                entry.StartDirectory = pending.StartDirectory;
                entry.LaunchOptions = pending.LaunchOptions;
            }
            else if (!CommandShortcut.Same(live, entry.Target, entry.LaunchOptions)
                     || !CommandShortcut.SameFolder(live.StartDirectory, entry.StartDirectory))
            {
                throw new InvalidOperationException($"{entry.Name}: its pending shortcut update could not be reconciled. Nothing was changed.");
            }

            // A separate full/current read has resolved the previous write to old or intended state.
            // No uncertain update is repeated without this observation.
            entry.PendingLaunch = null;
            _store.Save(entry);
        }

        if (!CommandShortcut.Same(live, entry.Target, entry.LaunchOptions)
            || !CommandShortcut.SameFolder(live.StartDirectory, entry.StartDirectory))
        {
            throw new InvalidOperationException($"{entry.Name}: its Steam shortcut was edited outside WSGM. Nothing was changed.");
        }
    }
}
