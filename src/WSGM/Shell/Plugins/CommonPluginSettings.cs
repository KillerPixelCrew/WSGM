using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Desired configuration delivery. Effective-state publications have no reference to this owner.</summary>
internal sealed class CommonPluginSettings
{
    private readonly PluginInstanceIdentity _identity;
    private readonly IConfigurablePlugin _plugin;
    private readonly IPluginConfigurationStore _store;

    /// <summary>Captures and validates a plugin's setting schema and its allowed choices.</summary>
    /// <param name="plugin">Borrowed configuration receiver; calls must be serialized by its registration.</param>
    /// <param name="store">Desired-preference store; effective-state publications never write it.</param>
    /// <param name="identity">Instance whose saved revisions are read and changed.</param>
    /// <exception cref="ArgumentException">The declared schema is invalid.</exception>
    internal CommonPluginSettings(IConfigurablePlugin plugin, IPluginConfigurationStore store,
        PluginInstanceIdentity identity)
    {
        var declared = plugin.Settings;
        if (!PluginConfigurationRules.IsValid(declared))
        {
            throw new ArgumentException("Plugin settings declaration is invalid.");
        }

        Schema = Array.AsReadOnly([
            .. declared.Select(setting => setting with
            {
                Choices = setting.Choices is null ? null : Array.AsReadOnly([.. setting.Choices])
            })
        ]);
        _plugin = plugin;
        _store = store;
        _identity = identity;
    }

    /// <summary>The last composed delivery, including defaults; null before a valid delivery is prepared.</summary>
    internal PluginConfiguration? Desired { get; private set; }

    /// <summary>The last delivery result; null before delivery, and unconfirmed while a delivery is outstanding.</summary>
    internal PluginConfigurationResult? Result { get; private set; }

    /// <summary>The captured read-only schema, with separate read-only copies of choice lists.</summary>
    internal IReadOnlyList<PluginSetting> Schema { get; }

    /// <summary>Delivers saved values and declared defaults without saving either the response or defaults.</summary>
    /// <param name="context">Current instance generation and delivery deadline.</param>
    /// <param name="cancellationToken">Cancels delivery; a dispatched configuration may already have taken effect.</param>
    /// <returns>The plugin's confirmation, or rejected/unconfirmed when the schema or confirmation is invalid.</returns>
    internal Task<PluginConfigurationResult> RestoreAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return DeliverAsync(_store.Read(_identity), PluginConfigurationOrigin.Restore, context, cancellationToken);
    }

    /// <summary>Delivers a newer saved revision once; the same revision returns its previous result without retrying.</summary>
    /// <param name="context">Current instance generation and delivery deadline.</param>
    /// <param name="cancellationToken">Cancels a new delivery; it does not undo a dispatched write.</param>
    /// <returns>The previous result, a new delivery result, or rejection when the saved revision moved backwards.</returns>
    internal Task<PluginConfigurationResult> RefreshAsync(PluginContext context, CancellationToken cancellationToken)
    {
        var saved = _store.Read(_identity);
        return Result switch
        {
            { } previous when previous.Revision == saved.Revision => Task.FromResult(previous),
            { } newer when saved.Revision < newer.Revision => Task.FromResult(new PluginConfigurationResult(
                newer.Revision,
                PluginConfigurationOutcome.Rejected, "Saved configuration revision moved backwards.")),
            _ => DeliverAsync(saved, PluginConfigurationOrigin.Restore, context, cancellationToken)
        };
    }

    /// <summary>Validates and saves explicit preference edits before delivering the resulting configuration once.</summary>
    /// <param name="expectedRevision">Saved revision against which this edit was made; stale edits throw.</param>
    /// <param name="changes">Declared keys and valid values to copy into the saved change set.</param>
    /// <param name="context">Current instance generation and delivery deadline.</param>
    /// <param name="cancellationToken">
    ///     Cancels before saving or during delivery; cancellation after save does not revert
    ///     preferences.
    /// </param>
    /// <returns>The delivery result. Rejected or unconfirmed delivery leaves the saved desired preferences intact.</returns>
    /// <exception cref="InvalidOperationException">The saved revision differs from the expected revision.</exception>
    /// <exception cref="ArgumentException">An edit or existing saved value does not match the current schema.</exception>
    internal async Task<PluginConfigurationResult> ChangeAsync(long expectedRevision,
        IReadOnlyDictionary<string, PluginValue> changes,
        PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, PluginValue> captured = new(changes, StringComparer.Ordinal);
        var current = _store.Read(_identity);
        if (current.Revision != expectedRevision)
        {
            throw new InvalidOperationException("Plugin preferences changed; refresh before editing.");
        }

        if (captured.Any(pair => Schema.FirstOrDefault(candidate => candidate.Key == pair.Key) is not { } setting
                                 || !PluginConfigurationRules.Accepts(setting, pair.Value)))
        {
            throw new ArgumentException("Preference changes do not match the declared schema.");
        }

        Dictionary<string, PluginValue> combined = new(current.Values, StringComparer.Ordinal);
        foreach (var pair in captured)
        {
            combined[pair.Key] = pair.Value;
        }

        if (!TryCompose(current with { Values = combined }, out _))
        {
            throw new ArgumentException("Saved preferences do not match the current schema.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Persist only the explicit change set. Defaults, readback and a plugin response never save.
        var saved = _store.Save(_identity, expectedRevision, captured);
        return await DeliverAsync(saved, PluginConfigurationOrigin.User, context, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PluginConfigurationResult> DeliverAsync(SavedPluginConfiguration saved,
        PluginConfigurationOrigin origin,
        PluginContext context, CancellationToken cancellationToken)
    {
        if (!TryCompose(saved, out var values))
        {
            return Result = new PluginConfigurationResult(saved.Revision, PluginConfigurationOutcome.Rejected,
                "Saved preferences no longer match the declaration.");
        }

        var desired = new PluginConfiguration(saved.Revision, origin, values);
        Desired = desired;
        Result = new PluginConfigurationResult(saved.Revision, PluginConfigurationOutcome.Unconfirmed,
            "Configuration delivery pending");
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _plugin.ConfigureAsync(desired, context, cancellationToken).ConfigureAwait(false);
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (result is null || result.Revision != saved.Revision || !Enum.IsDefined(result.Outcome))
        {
            return Result = new PluginConfigurationResult(saved.Revision, PluginConfigurationOutcome.Unconfirmed,
                "The plugin returned an invalid configuration confirmation.");
        }

        return Result = result;
    }

    private bool TryCompose(SavedPluginConfiguration saved, out IReadOnlyDictionary<string, PluginValue> values)
    {
        Dictionary<string, PluginValue> configured = new(StringComparer.Ordinal);
        foreach (var setting in Schema)
        {
            var value = saved.Values.TryGetValue(setting.Key, out var stored) ? stored : setting.Default;
            if (!PluginConfigurationRules.Accepts(setting, value))
            {
                values = configured;
                return false;
            }

            configured.Add(setting.Key, value);
        }

        values = new ReadOnlyDictionary<string, PluginValue>(configured);
        return true;
    }
}
