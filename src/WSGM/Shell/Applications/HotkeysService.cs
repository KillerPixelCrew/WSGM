using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The session owner of the Overlay hotkeys: the list in <see cref="AppConfig.Hotkeys" />, its edits and the
///     sends. Not <see cref="HotkeyService" />, which listens for the shortcut that opens the Overlay.
/// </summary>
/// <remarks>
///     A send runs after the Overlay has closed and released its input lease. It types only into a game: when
///     WSGM, Steam, a launcher or the desktop is in front it sends nothing and says so on the hotkey.
/// </remarks>
internal sealed class HotkeysService : IHotkeysSource
{
    /// <summary>The Quick access pin prefix; a hotkey pins as <c>hotkey:{id}</c>.</summary>
    internal const string PinPrefix = "hotkey:";

    /// <summary>What a press reports when no game was in front.</summary>
    internal const string NoGameNotice =
        "No game was in front, so no keys were sent. Start the game, then use the hotkey again.";

    // How long Windows gets to bring the game back before the foreground is checked again.
    private static readonly TimeSpan ReturnWait = TimeSpan.FromMilliseconds(150);

    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly IHotkeyDesktop _desktop;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Notice, string Summary)> _notices = new(StringComparer.Ordinal);
    private readonly ConfigStore _store;
    private HotkeyDefinition[] _definitions;

    /// <summary>Reads the stored hotkeys.</summary>
    /// <param name="store">The persistence owner.</param>
    /// <param name="config">The session's configuration.</param>
    /// <param name="desktop">The desktop sends go through; null is the live one.</param>
    internal HotkeysService(ConfigStore store, AppConfig config, IHotkeyDesktop? desktop = null)
    {
        _store = store;
        _desktop = desktop ?? HotkeyDesktop.Instance;
        _definitions = Detach(config.Hotkeys);
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public HotkeyState[] ReadState()
    {
        lock (_gate)
        {
            return
            [
                .. _definitions.Select(definition =>
                    _notices.TryGetValue(definition.Id, out var notice)
                        ? new HotkeyState(definition.Copy(), notice.Notice, notice.Summary)
                        : new HotkeyState(definition.Copy(), ""))
            ];
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(HotkeyDefinition definition)
    {
        var name = definition.Name.Trim();
        if (name.Length == 0)
        {
            return Task.FromException(new InvalidOperationException("Enter a name for the hotkey."));
        }

        var keys = definition.Keys.Where(HotkeyKeys.IsValid).Distinct().ToList();
        if (keys.Count == 0)
        {
            return Task.FromException(new InvalidOperationException("Choose at least one key."));
        }

        var entry = definition.Copy() with
        {
            Name = name,
            Keys = keys,
            PressDelayMs = Math.Clamp(definition.PressDelayMs, 0, HotkeyDefinition.MaximumPressDelayMs)
        };
        return CommandAsync(() =>
        {
            Persist(config =>
            {
                var index = config.Hotkeys.FindIndex(existing => existing.Id == entry.Id);
                if (index < 0)
                {
                    config.Hotkeys.Add(entry);
                }
                else
                {
                    config.Hotkeys[index] = entry;
                }
            });
            lock (_gate)
            {
                // Edited keys have not been pressed yet, so the last press's refusal no longer applies.
                _notices.Remove(entry.Id);
            }

            Log.Info($"Hotkeys: saved '{entry.Name}' ({HotkeyKeys.Describe(entry.Keys)}).");
        });
    }

    /// <inheritdoc />
    public Task RemoveAsync(string id)
    {
        return CommandAsync(() =>
        {
            Persist(config =>
            {
                config.Hotkeys.RemoveAll(entry => entry.Id == id);
                config.QuickAccessPins.RemoveAll(pin => pin == PinPrefix + id);
            });
            lock (_gate)
            {
                _notices.Remove(id);
            }
        });
    }

    /// <inheritdoc />
    public async Task SendAsync(string id, nint returnTo, CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HotkeyDefinition? definition;
            lock (_gate)
            {
                definition = _definitions.FirstOrDefault(entry => entry.Id == id)?.Copy();
            }

            if (definition is null)
            {
                Log.Info($"Hotkeys: '{id}' was removed before it was sent.");
                return;
            }

            var chord = HotkeyKeys.Describe(definition.Keys);
            var target = _desktop.Describe(_desktop.Foreground());
            if (target is null && _desktop.Describe(returnTo) is { } previous)
            {
                // Windows gave the foreground to something else as the Overlay closed: bring back the window it
                // opened over, then check again rather than typing into whatever is there.
                _desktop.BringToFront(previous.Window);
                await Task.Delay(ReturnWait, cancellationToken).ConfigureAwait(false);
                target = _desktop.Describe(_desktop.Foreground());
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (target is null)
            {
                Log.Info($"Hotkeys: '{definition.Name}' ({chord}) not sent; no game is in front.");
                Report(id, NoGameNotice, "Not sent: no game in front");
                return;
            }

            var result = _desktop.Send([.. definition.Keys.Select(key => (uint)key)], definition.PressDelayMs);
            if (!result.Complete)
            {
                Log.Warn($"Hotkeys: '{definition.Name}' ({chord}) to {target.Executable}: Windows accepted "
                         + $"{result.Sent} of {result.Requested} key events (Win32 error {result.Error}).");
                Report(id, $"Windows accepted only {result.Sent} of {result.Requested} key events, so "
                           + $"{target.Executable} may not have received {chord}.",
                    "Windows refused some keys");
                return;
            }

            Log.Info($"Hotkeys: sent '{definition.Name}' ({chord}) to {target.Executable} (0x{target.Window:X}).");
            Report(id, "", "");
        }
        finally
        {
            _commands.Release();
        }
    }

    /// <inheritdoc />
    public void ReportFailure(string id, string message)
    {
        Report(id, message, "Not sent: see Hotkeys");
    }

    /// <summary>Follows a reloaded configuration.</summary>
    /// <param name="config">The configuration now in force.</param>
    internal void ConfigurationChanged(AppConfig config)
    {
        lock (_gate)
        {
            _definitions = Detach(config.Hotkeys);
        }

        Changed?.Invoke();
    }

    private static HotkeyDefinition[] Detach(IEnumerable<HotkeyDefinition> entries)
    {
        return [.. entries.Select(entry => entry.Copy())];
    }

    private void Report(string id, string notice, string summary)
    {
        lock (_gate)
        {
            if (notice.Length == 0)
            {
                _notices.Remove(id);
            }
            else
            {
                _notices[id] = (notice, summary);
            }
        }

        Changed?.Invoke();
    }

    private async Task CommandAsync(Action operation)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(operation).ConfigureAwait(false);
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
}
