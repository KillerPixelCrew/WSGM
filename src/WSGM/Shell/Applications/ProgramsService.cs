using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>One session owner for Programs configuration, exact process matching and launch/focus actions.</summary>
internal sealed class ProgramsService : IProgramsSource, IAsyncDisposable
{
    internal const string PinPrefix = "program:";
    private const int ErrorCancelled = 1223;

    /// <summary>How many quarter seconds a packaged application gets to show its window after activation.</summary>
    private const int PackagedWindowAttempts = 40;

    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Lock _gate = new();

    private readonly Dictionary<string, (long Length, DateTime Written, string Hash)> _hashes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly IInstalledApplicationHost _installed;
    private readonly Dictionary<string, nint> _lastWindows = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Task _reconciliation;
    private readonly ConfigStore _store;
    private ProgramDefinition[] _definitions;
    private HashSet<string> _pins;
    private ProgramState[] _state = [];

    /// <summary>Creates the owner and starts its reconciliation.</summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="config">The current configuration.</param>
    /// <param name="installed">Windows' installed and packaged applications; null uses the real host.</param>
    internal ProgramsService(ConfigStore store, AppConfig config, IInstalledApplicationHost? installed = null)
    {
        _store = store;
        _installed = installed ?? new WindowsInstalledApplicationHost(store.Context);
        _definitions = config.Programs.Select(entry => entry with { }).ToArray();
        _pins = config.QuickAccessPins.ToHashSet(StringComparer.Ordinal);
        _reconciliation = Task.Run(ReconcileLoopAsync);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _reconciliation.ConfigureAwait(false);
        await _commands.WaitAsync().ConfigureAwait(false);
        foreach (var bitmap in _icons.Values)
        {
            bitmap?.Dispose();
        }

        _commands.Release();
        _lifetime.Dispose();
    }

    public event Action? Changed;

    public ProgramState[] ReadState()
    {
        lock (_gate)
        {
            return _state.Select(state => state with { Definition = state.Definition with { } }).ToArray();
        }
    }

    /// <summary>Only explicit executable selection or replacement approval updates the launch fingerprint.</summary>
    public Task SaveAsync(ProgramDefinition definition, bool approveExecutable)
    {
        return CommandAsync(() =>
        {
            var name = definition.Name.Trim();
            if (name.Length == 0)
            {
                throw new InvalidOperationException("Enter a program name.");
            }

            // Only an explicit selection changes what a program launches; a packaged one has no executable.
            var packaged = approveExecutable ? ValidatePackaged(definition.AppUserModelId) : "";
            var executable = approveExecutable && packaged.Length == 0 ? ValidateExecutable(definition.Executable) : "";
            var hash = approveExecutable && packaged.Length == 0 ? Hash(executable, true) : "";
            if (definition.WorkingDirectory.Length > 0 && !Directory.Exists(definition.WorkingDirectory))
            {
                throw new InvalidOperationException("The working directory is unavailable.");
            }

            Persist(config =>
            {
                var index = config.Programs.FindIndex(existing => existing.Id == definition.Id);
                if (index < 0 && !approveExecutable)
                {
                    throw new InvalidOperationException("This program was removed.");
                }

                // Identities come from the stored entry: only executable approval or explicit UI selection
                // changes them, never a stale editor snapshot.
                var stored = index < 0 ? null : config.Programs[index];
                var entry = definition with
                {
                    Name = name,
                    AppUserModelId = approveExecutable ? packaged : stored!.AppUserModelId,
                    Executable = approveExecutable ? executable : stored!.Executable,
                    ExecutableHash = approveExecutable ? hash : stored!.ExecutableHash,
                    UiExecutable = approveExecutable ? "" : stored!.UiExecutable,
                    UiExecutableHash = approveExecutable ? "" : stored!.UiExecutableHash
                };
                if (index < 0)
                {
                    config.Programs.Add(entry);
                }
                else
                {
                    config.Programs[index] = entry;
                }
            });
        });
    }

    public Task RemoveAsync(string id)
    {
        return CommandAsync(() => Persist(config =>
        {
            config.Programs.RemoveAll(entry => entry.Id == id);
            config.QuickAccessPins.RemoveAll(pin => pin == PinPrefix + id);
            _pending.Remove(id);
        }));
    }

    public bool IsPinned(string id)
    {
        lock (_gate)
        {
            return _pins.Contains(PinPrefix + id);
        }
    }

    public string LastResult { get; private set; } = "";

    public void ReportResult(string message)
    {
        LastResult = message;
        Log.Info($"Programs: {message}");
        Changed?.Invoke();
    }

    /// <summary>User-assisted bootstrap resolution: retain only the selected HWND's verified current image.</summary>
    public Task RememberWindowAsync(string id, ProgramUiCandidate candidate)
    {
        return CommandAsync(() =>
        {
            var window = candidate.Window;
            var process = ReadProcess(window.ProcessId)
                          ?? throw new InvalidOperationException(
                              "The selected application closed or cannot be inspected.");
            if (process.Started != candidate.Started || !string.Equals(process.Path, candidate.Executable,
                    StringComparison.OrdinalIgnoreCase) ||
                !WindowFinder.ListSwitchableWindows().Any(current => current.Hwnd == window.Hwnd &&
                                                                     current.ProcessId == window.ProcessId))
            {
                throw new InvalidOperationException("The selected application window changed. Choose it again.");
            }

            var hash = Hash(ValidateExecutable(process.Path), true);
            Persist(config =>
            {
                var entry = Stored(config, id);
                entry.UiExecutable = process.Path;
                entry.UiExecutableHash = hash;
            });
            _pending.Remove(id);
        });
    }

    public Task ForgetUiAsync(string id)
    {
        return CommandAsync(() => Persist(config =>
        {
            var entry = Stored(config, id);
            entry.UiExecutable = "";
            entry.UiExecutableHash = "";
        }));
    }

    public Task ClearPendingAsync(string id)
    {
        return CommandAsync(() => _pending.Remove(id));
    }

    /// <summary>Called only after Overlay closure and input-lease release, on a worker thread.</summary>
    public async Task<string> ActivateAsync(string id, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var entry = _store.Read().RequireConfig().Programs.FirstOrDefault(item => item.Id == id)
                        ?? throw new InvalidOperationException("This program was removed.");
            if (entry.AppUserModelId.Length > 0)
            {
                return await ActivatePackagedAsync(entry, token).ConfigureAwait(false);
            }

            var processes = CaptureProcesses([entry]);
            var matched = Match(entry, processes);
            var window = FindWindow(entry.Id, matched, WindowFinder.ListSwitchableWindows(),
                NativeMethods.GetForegroundWindow());
            if (window is not null)
            {
                return await FocusAsync(entry.Id, window, token).ConfigureAwait(false);
            }

            var unknown = HasUnknownCandidate(entry, processes);
            if (unknown)
            {
                return "Process identity could not be verified. No additional instance was started.";
            }

            if (_pending.Contains(id))
            {
                return
                    "A launch was already dispatched. Select its UI application or explicitly clear the pending launch in Programs.";
            }

            if (matched.Count > 0)
            {
                // Give an existing UI a bounded chance to appear before considering explicit IPC policy.
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    window = FindWindow(entry.Id, Match(entry, CaptureProcesses([entry])),
                        WindowFinder.ListSwitchableWindows(), NativeMethods.GetForegroundWindow());
                    if (window is not null)
                    {
                        return await FocusAsync(entry.Id, window, token).ConfigureAwait(false);
                    }
                }

                if (!entry.ReactivateWithoutWindow)
                {
                    return
                        "Running, but no eligible application window is available. No additional instance was started.";
                }
            }

            if (!Approved(entry.Executable, entry.ExecutableHash, true))
            {
                return
                    "Executable missing or changed. Choose an executable or approve its replacement in Programs before launching.";
            }

            if (entry.WorkingDirectory.Length > 0 && !Directory.Exists(entry.WorkingDirectory))
            {
                return "The working directory is unavailable. Edit it in Programs before launching.";
            }

            if (entry.UiExecutable.Length > 0 && !Approved(entry.UiExecutable, entry.UiExecutableHash, true))
            {
                return
                    "Resolved UI executable missing or changed. Select the running UI again or forget the UI identity before launching.";
            }

            token.ThrowIfCancellationRequested();
            _pending.Add(id); // Dispatch uncertainty must never become an automatic duplicate attempt.
            if (entry.Elevated)
            {
                try
                {
                    using var launched = Process.Start(new ProcessStartInfo(entry.Executable, entry.Arguments)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WorkingDirectory = WorkingDirectory(entry)
                    });
                }
                catch (Win32Exception ex)
                {
                    // ShellExecuteEx refused before creating a process, so nothing is pending.
                    _pending.Remove(id);
                    return ex.NativeErrorCode == ErrorCancelled
                        ? "Administrator elevation was declined. Nothing was started."
                        : $"Windows did not start the program: {ex.Message}";
                }
            }
            else
            {
                var result = await UnelevatedLauncher.TryStartViaScheduledTaskAsync(_store.Context,
                    entry.Executable, entry.Arguments, DateTimeOffset.UtcNow.AddSeconds(10), WorkingDirectory(entry),
                    token).ConfigureAwait(false);
                if (result == ScheduledTaskLaunchDisposition.NotDispatched)
                {
                    _pending.Remove(id);
                    return
                        "Windows did not dispatch the normal-user launch. Enable elevation explicitly only if this tool requires it.";
                }
            }

            for (var attempt = 0; attempt < 16; attempt++)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                matched = Match(entry, CaptureProcesses([entry]));
                window = FindWindow(entry.Id, matched, WindowFinder.ListSwitchableWindows(),
                    NativeMethods.GetForegroundWindow());
                if (window is not null)
                {
                    return await FocusAsync(entry.Id, window, token).ConfigureAwait(false);
                }
            }

            return
                "Launch dispatched; no matching UI window appeared. For bootstrap apps, use Select running UI application in Programs.";
        }
        finally
        {
            try
            {
                Refresh();
            }
            finally
            {
                _commands.Release();
            }
        }
    }

    /// <inheritdoc />
    public async Task<InstalledProgramList> ReadInstalledAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new InstalledProgramList(await _installed.ListAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            Log.Warn($"Programs: installed applications could not be read: {ex.Message}");
            return new InstalledProgramList([], "Windows could not list the installed apps.");
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, Bitmap?>> ReadInstalledIconsAsync(IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        return _installed.ReadIconsAsync(keys, cancellationToken);
    }

    /// <summary>Focuses a packaged application's window, or activates it through the de-elevated launcher.</summary>
    /// <remarks>
    ///     Windows activates a running packaged application instead of starting a second one, so repeating the
    ///     request cannot duplicate it and this needs none of a desktop program's pending-launch guard.
    /// </remarks>
    private async Task<string> ActivatePackagedAsync(ProgramDefinition entry, CancellationToken token)
    {
        var window = FindPackagedWindow(entry);
        if (window is not null)
        {
            return await FocusAsync(entry.Id, window, token).ConfigureAwait(false);
        }

        if (!_installed.IsInstalled(entry.AppUserModelId))
        {
            return "This app is no longer installed. Remove it or add it again in Programs.";
        }

        token.ThrowIfCancellationRequested();
        if (await _installed.ActivateAsync(entry.AppUserModelId, token).ConfigureAwait(false)
            == ScheduledTaskLaunchDisposition.NotDispatched)
        {
            return "Windows did not dispatch the normal-user launch.";
        }

        for (var attempt = 0; attempt < PackagedWindowAttempts; attempt++)
        {
            await Task.Delay(250, token).ConfigureAwait(false);
            window = FindPackagedWindow(entry);
            if (window is not null)
            {
                return await FocusAsync(entry.Id, window, token).ConfigureAwait(false);
            }
        }

        return "Launch dispatched; its window has not appeared yet.";
    }

    private AppWindow? FindPackagedWindow(ProgramDefinition entry)
    {
        return PickWindow(entry.Id, PackagedWindows(entry, WindowFinder.ListSwitchableWindows()),
            NativeMethods.GetForegroundWindow());
    }

    private AppWindow[] PackagedWindows(ProgramDefinition entry, IEnumerable<AppWindow> switchable)
    {
        return switchable.Where(window => string.Equals(_installed.WindowApplicationId(window),
            entry.AppUserModelId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private string ValidatePackaged(string appUserModelId)
    {
        var id = appUserModelId.Trim();
        if (id.Length == 0)
        {
            return "";
        }

        if (!PackagedLaunchCommand.ValidAumid(id) || !_installed.IsInstalled(id))
        {
            throw new InvalidOperationException("That app is not installed.");
        }

        return id;
    }

    internal void ConfigurationChanged(AppConfig config)
    {
        lock (_gate)
        {
            _definitions = config.Programs.Select(entry => entry with { }).ToArray();
            _pins = config.QuickAccessPins.ToHashSet(StringComparer.Ordinal);
        }

        Changed?.Invoke();
    }

    private static ProgramDefinition Stored(AppConfig config, string id)
    {
        return config.Programs.FirstOrDefault(item => item.Id == id)
               ?? throw new InvalidOperationException("This program was removed.");
    }

    internal static ProgramUiCandidate[] SelectableWindows()
    {
        return WindowFinder.ListSwitchableWindows()
            .Select(window => (Window: window, Process: ReadProcess(window.ProcessId)))
            .Where(item => item.Process is { Path.Length: > 0 })
            .Select(item => new ProgramUiCandidate(item.Window, item.Process!.Path, item.Process.Started)).ToArray();
    }

    private async Task<string> FocusAsync(string id, AppWindow window, CancellationToken token)
    {
        // HWND reuse and process exit between snapshot and activation must not focus another app.
        NativeMethods.GetWindowThreadProcessId(window.Hwnd, out var owner);
        var definition = _definitions.FirstOrDefault(entry => entry.Id == id);
        if (owner != window.ProcessId || definition is null || !StillBelongs(definition, window, owner))
        {
            return "The application window changed. Invoke the program again.";
        }

        token.ThrowIfCancellationRequested();
        WindowFinder.BringToForeground(window.Hwnd);
        await Task.Delay(150, token).ConfigureAwait(false);
        // The application may hand activation to its own owned or modal window.
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var foreground);
        if (foreground != owner)
        {
            return "Windows or the foreground game prevented application activation.";
        }

        _lastWindows[id] = window.Hwnd;
        _pending.Remove(id);
        return "Application focused.";
    }

    private bool StillBelongs(ProgramDefinition definition, AppWindow window, uint owner)
    {
        return definition.AppUserModelId.Length > 0
            ? PackagedWindows(definition, [window]).Length > 0
            : Match(definition, CaptureProcesses([definition])).Any(process => process.Id == owner);
    }

    /// <summary>Picks a program's window from one window pass.</summary>
    /// <param name="id">The program.</param>
    /// <param name="processes">Its matched processes.</param>
    /// <param name="switchable">Every switchable window of this pass, in top-level Z order.</param>
    /// <param name="foreground">The foreground window of this pass.</param>
    /// <returns>The foreground window, then the last one focused, then the topmost, or null.</returns>
    private AppWindow? FindWindow(string id, IReadOnlyList<ProgramProcess> processes,
        IReadOnlyList<AppWindow> switchable, nint foreground)
    {
        var ids = processes.Select(process => process.Id).ToHashSet();
        return PickWindow(id, switchable.Where(window => ids.Contains(window.ProcessId)).ToArray(), foreground);
    }

    private AppWindow? PickWindow(string id, AppWindow[] windows, nint foreground)
    {
        _lastWindows.TryGetValue(id, out var last);
        var active = windows.FirstOrDefault(window => window.Hwnd == foreground);
        if (active is not null)
        {
            _lastWindows[id] = active.Hwnd;
        }

        return active
               ?? windows.FirstOrDefault(window => window.Hwnd == last)
               ??
               windows.FirstOrDefault(); // EnumWindows is in top-level Z order, filtered for visible non-tool windows.
    }

    private List<ProgramProcess> Match(ProgramDefinition entry, ProgramProcess[] processes)
    {
        // A resolved UI replaces the bootstrap process as the application identity. This also lets
        // a persistent launcher remain alive after its real UI has exited without blocking relaunch.
        var path = entry.UiExecutable.Length > 0 ? entry.UiExecutable : entry.Executable;
        var hash = entry.UiExecutable.Length > 0 ? entry.UiExecutableHash : entry.ExecutableHash;
        // The on-disk file may be missing/replaced while its original image is still running.
        // Exact live image identity still authorizes focus; fingerprints gate the next launch.
        return path.Length > 0 && hash.Length > 0
            ? processes.Where(process => string.Equals(process.Path, path, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
    }

    private static bool HasUnknownCandidate(ProgramDefinition entry, IEnumerable<ProgramProcess> processes)
    {
        return processes.Any(process => process.Path.Length == 0 &&
                                        (string.Equals(process.Name, Path.GetFileNameWithoutExtension(entry.Executable),
                                             StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(process.Name,
                                             Path.GetFileNameWithoutExtension(entry.UiExecutable),
                                             StringComparison.OrdinalIgnoreCase)));
    }

    private async Task CommandAsync(Action operation)
    {
        await _commands.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await Task.Run(operation, _lifetime.Token).ConfigureAwait(false);
            Refresh();
        }
        finally
        {
            _commands.Release();
        }
    }

    private void Persist(Action<AppConfig> mutation)
    {
        var config = _store.Update(current =>
        {
            mutation(current);
            return true;
        });
        ConfigurationChanged(config);
    }

    private async Task ReconcileLoopAsync()
    {
        try
        {
            do
            {
                await _commands.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    Refresh();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Change("programs.refresh", $"Programs reconciliation unavailable: {ex.Message}");
                }
                finally
                {
                    _commands.Release();
                }

                await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token).ConfigureAwait(false);
            } while (!_lifetime.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Refresh()
    {
        ProgramDefinition[] definitions;
        lock (_gate)
        {
            definitions = _definitions;
        }

        var processes = definitions.Length == 0 ? [] : CaptureProcesses(definitions);
        // One window pass for every running program, taken only once one is running.
        List<AppWindow>? switchable = null;
        nint foreground = 0;
        var states = definitions.Select(entry =>
        {
            if (entry.AppUserModelId.Length > 0)
            {
                switchable ??= WindowFinder.ListSwitchableWindows();
                return PackagedState(entry, switchable);
            }

            var available = Approved(entry.Executable, entry.ExecutableHash, false);
            var uiAvailable = entry.UiExecutable.Length == 0 ||
                              Approved(entry.UiExecutable, entry.UiExecutableHash, false);
            var matched = Match(entry, processes);
            var running = matched.Count > 0;
            if (running && switchable is null)
            {
                switchable = WindowFinder.ListSwitchableWindows();
                foreground = NativeMethods.GetForegroundWindow();
            }

            var window = running ? FindWindow(entry.Id, matched, switchable!, foreground) : null;
            if (window is not null)
            {
                _pending.Remove(entry.Id);
            }

            var runningStatus = window is null ? "Running · no window" : "Running";
            var status = running
                ? available && uiAvailable ? runningStatus : runningStatus + " · launch unavailable"
                : HasUnknownCandidate(entry, processes)
                    ? "Identity unavailable"
                    : _pending.Contains(entry.Id)
                        ? "Launch pending · select UI or clear pending"
                        : !available
                            ? "Unavailable · executable missing or changed"
                            : !uiAvailable
                                ? "UI executable changed · select running UI again"
                                : "Not running";
            return new ProgramState(entry with { }, running, available, status, Icon(entry), uiAvailable);
        }).ToArray();
        bool changed;
        lock (_gate)
        {
            changed = !_state.SequenceEqual(states);
            _state = states;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private ProgramState PackagedState(ProgramDefinition entry, IReadOnlyList<AppWindow> switchable)
    {
        var running = PackagedWindows(entry, switchable).Length > 0;
        var available = running || _installed.IsInstalled(entry.AppUserModelId);
        var status = running ? "Running" : available ? "Not running" : "Unavailable · app not installed";
        return new ProgramState(entry with { }, running, available, status, PackagedIcon(entry));
    }

    private Bitmap? PackagedIcon(ProgramDefinition entry)
    {
        if (_icons.TryGetValue(entry.AppUserModelId, out var cached))
        {
            return cached;
        }

        // Called on the reconciliation worker; the read itself runs on its own STA thread.
        var icons = _installed.ReadIconsAsync([entry.AppUserModelId], _lifetime.Token).GetAwaiter().GetResult();
        return _icons[entry.AppUserModelId] = icons.GetValueOrDefault(entry.AppUserModelId);
    }

    private Bitmap? Icon(ProgramDefinition entry)
    {
        var key = entry.Executable + "|" + entry.ExecutableHash;
        if (_icons.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bitmap = File.Exists(entry.Executable) ? IconRasterizer.FromExecutable(entry.Executable, 32) : null;
        _icons[key] = bitmap;
        return bitmap;
    }

    private bool Approved(string path, string hash, bool fresh)
    {
        try
        {
            return hash.Length > 0 && string.Equals(Hash(path, fresh), hash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private string Hash(string path, bool fresh)
    {
        var file = new FileInfo(path);
        if (!fresh && _hashes.TryGetValue(path, out var cached) && cached.Length == file.Length &&
            cached.Written == file.LastWriteTimeUtc)
        {
            return cached.Hash;
        }

        using var input = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(input));
        _hashes[path] = (file.Length, file.LastWriteTimeUtc, hash);
        return hash;
    }

    private static string ValidateExecutable(string path)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!File.Exists(full) || !string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose an existing Windows executable (.exe).");
        }

        using var input = File.OpenRead(full);
        if (input.ReadByte() != 'M' || input.ReadByte() != 'Z')
        {
            throw new InvalidOperationException("The selected file is not a Windows executable.");
        }

        return full;
    }

    private static string WorkingDirectory(ProgramDefinition entry)
    {
        return entry.WorkingDirectory.Length > 0
            ? entry.WorkingDirectory
            : Path.GetDirectoryName(entry.Executable)!;
    }

    private static ProgramProcess? ReadProcess(uint id)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)id));
            return ReadProcess(process, true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Reconciliation reads only the image path; a birth time is needed only for UI selection.</summary>
    private static ProgramProcess? ReadProcess(Process process, bool started)
    {
        var name = "";
        try
        {
            name = process.ProcessName;
            if (process.SessionId != ProcessLookup.CurrentSessionId || process.HasExited)
            {
                return null;
            }

            var id = checked((uint)process.Id);
            return new ProgramProcess(id, NativeShellProcess.TryGetImagePath(id) ?? "", name,
                started ? process.StartTime.ToUniversalTime() : default);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An uninspectable same-name candidate can block launching but can never authorize focus.
            return name.Length > 0 ? new ProgramProcess(checked((uint)process.Id), "", name) : null;
        }
    }

    /// <summary>Inspects only processes whose image name a definition can match or be blocked by.</summary>
    private static ProgramProcess[] CaptureProcesses(IEnumerable<ProgramDefinition> definitions)
    {
        // Matching compares exact image paths and HasUnknownCandidate compares names, so a process
        // with any other name is never opened for its session, exit state or image path.
        var names = definitions.SelectMany(entry => new[] { entry.Executable, entry.UiExecutable })
            .Where(path => path.Length > 0).Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<ProgramProcess> result = [];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (names.Contains(process.ProcessName) && ReadProcess(process, false) is { } captured)
                {
                    result.Add(captured);
                }
            }
        }

        return result.ToArray();
    }

    private sealed record ProgramProcess(uint Id, string Path, string Name, DateTime Started = default);
}
