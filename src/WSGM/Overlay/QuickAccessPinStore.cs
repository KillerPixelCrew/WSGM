using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>
///     The pin list a controller shows and the saves of its changes to AppConfig.QuickAccessPins, in press
///     order.
/// </summary>
/// <remarks>
///     Seeded from config and replaced on reload; a toggle never writes the shared config object, and its
///     save is chained behind the previous one so the file ends in press order. A preview surface never
///     writes.
/// </remarks>
internal sealed class QuickAccessPinStore
{
    private readonly bool _previewOnly;
    private readonly ConfigStore _store;

    /// <summary>Reads the stored pins.</summary>
    /// <param name="config">The initial configuration.</param>
    /// <param name="store">The persistence owner.</param>
    /// <param name="previewOnly">Whether this surface is a preview that never writes.</param>
    internal QuickAccessPinStore(AppConfig config, ConfigStore store, bool previewOnly)
    {
        _store = store;
        _previewOnly = previewOnly;
        Ids = Read(config);
    }

    /// <summary>The pins in display order.</summary>
    internal List<string> Ids { get; private set; }

    /// <summary>Completes after the pin changes submitted so far have finished saving.</summary>
    internal Task Writes { get; private set; } = Task.CompletedTask;

    /// <summary>Takes a reloaded configuration's pins once no toggle is still waiting for the store.</summary>
    /// <param name="config">The freshly loaded configuration.</param>
    internal void Reload(AppConfig config)
    {
        // A reload can echo an earlier save while a later toggle is still waiting for the store.
        // Keep that pending intent as the next toggle's baseline until the write chain has settled.
        if (Writes.IsCompleted)
        {
            Ids = Read(config);
        }
    }

    /// <summary>Pins an unpinned id or unpins a pinned one.</summary>
    /// <param name="id">Stable section or capability pin ID.</param>
    /// <returns>The new list, which <see cref="Save" /> then stores.</returns>
    internal List<string> Toggle(string id)
    {
        List<string> pins = [.. Ids];
        if (!pins.Remove(id))
        {
            pins.Add(id);
        }

        Ids = pins;
        return pins;
    }

    /// <summary>Logs the list and queues its save behind the earlier ones; a preview only logs.</summary>
    internal void Save()
    {
        Log.Info($"Quick access pins: {string.Join(", ", Ids)}.");
        if (_previewOnly)
        {
            return;
        }

        string[] snapshot = [.. Ids];
        Writes = Writes.ContinueWith(_ => Write(snapshot), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static List<string> Read(AppConfig config)
    {
        return [.. config.QuickAccessPins.Select(QuickAccessPins.CurrentId).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Stores one pin list; a failure is logged and the next toggle still saves its own list.</summary>
    private void Write(string[] pins)
    {
        try
        {
            _store.Update(config =>
            {
                if (config.QuickAccessPins.SequenceEqual(pins, StringComparer.Ordinal))
                {
                    return false;
                }

                config.QuickAccessPins = [.. pins];
                return true;
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save quick access pins: {ex.Message}");
        }
    }
}
