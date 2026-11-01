using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Which installed themes the store lists, and which of them it has a newer version of.</summary>
/// <remarks>
///     The answer comes from the last check and is replaced as a whole by the next one, so a reader holds a
///     consistent set. A check that cannot reach the store keeps the previous answer.
/// </remarks>
internal sealed class ThemeUpdateCheck
{
    private readonly ThemeStoreClient _client;
    private readonly Lock _gate = new();
    private readonly Action _publish;

    private IReadOnlyDictionary<string, (string Status, string? Latest, string? Id)> _updates =
        new Dictionary<string, (string Status, string? Latest, string? Id)>(StringComparer.Ordinal);

    /// <summary>Creates the check; nothing is known until it first runs.</summary>
    /// <param name="client">The store.</param>
    /// <param name="publish">Publishes a changed answer.</param>
    internal ThemeUpdateCheck(ThemeStoreClient client, Action publish)
    {
        _client = client;
        _publish = publish;
    }

    /// <summary>The last answer by installed theme name: its state, the store's newer version and its store id.</summary>
    internal IReadOnlyDictionary<string, (string Status, string? Latest, string? Id)> Updates
    {
        get
        {
            lock (_gate)
            {
                return _updates;
            }
        }
    }

    /// <summary>The store id of a theme the store has a newer version of.</summary>
    /// <param name="name">The installed theme.</param>
    /// <returns>The id, or null when the theme is current, local or unknown.</returns>
    internal string? OutdatedId(string name)
    {
        return Updates.TryGetValue(name, out var update) && update.Status == ThemeStates.Outdated ? update.Id : null;
    }

    /// <summary>Every theme the store has a newer version of.</summary>
    /// <returns>The installed names with their store ids.</returns>
    internal List<(string Name, string Id)> Outdated()
    {
        return
        [
            .. Updates.Where(pair => pair.Value.Status == ThemeStates.Outdated && pair.Value.Id is not null)
                .Select(pair => (pair.Key, pair.Value.Id!))
        ];
    }

    /// <summary>Drops what is known about a theme that was deleted.</summary>
    /// <param name="name">The theme.</param>
    internal void Forget(string name)
    {
        lock (_gate)
        {
            if (!_updates.ContainsKey(name))
            {
                return;
            }

            _updates = _updates.Where(pair => pair.Key != name)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
    }

    /// <summary>Asks the store about every installed theme and publishes the answer.</summary>
    /// <param name="installed">The installed themes' names, store ids and versions.</param>
    /// <param name="cancellationToken">Cancels the store request.</param>
    /// <returns>A task that completes once the answer is published, kept, or the check was cancelled.</returns>
    internal async Task CheckAsync(IReadOnlyList<(string Name, string Id, string Version)> installed,
        CancellationToken cancellationToken)
    {
        if (installed.Count == 0)
        {
            lock (_gate)
            {
                _updates = new Dictionary<string, (string, string?, string?)>(StringComparer.Ordinal);
            }

            _publish();
            return;
        }

        IReadOnlyList<ThemeStoreSummary> remote;
        try
        {
            remote = await _client.LookUpAsync(
                    [.. installed.Select(theme => theme.Id).Distinct(StringComparer.Ordinal)], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ThemeStoreException ex)
        {
            Log.Warn($"Themes: the update check could not reach the store: {ex.Message}");
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Dictionary<string, (string Status, string? Latest, string? Id)> updates = new(StringComparer.Ordinal);
        foreach (var (name, id, version) in installed)
        {
            var entry = remote.FirstOrDefault(candidate => candidate.Id == id || candidate.Name == id);
            updates[name] = entry is null
                ? (ThemeStates.Local, null, null)
                : entry.Version == version
                    ? (ThemeStates.Installed, null, entry.Id)
                    : (ThemeStates.Outdated, entry.Version, entry.Id);
        }

        lock (_gate)
        {
            _updates = updates;
        }

        _publish();
    }
}
