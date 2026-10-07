using System;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>
///     One registry value whose pre-WSGM state is snapshotted into AppConfig so
///     it can be restored exactly on uninstall — including "value was absent" and the
///     original value kind (REG_EXPAND_SZ / REG_QWORD survive the round trip, and an
///     empty value stays distinguishable from a missing one). The four persisted
///     AppConfig properties are bound through the load/store delegates: their JSON
///     names are load-bearing on installed devices, so this type never names them
///     itself and they must never change.
/// </summary>
/// <typeparam name="T">Persisted representation of the registry value after coercion.</typeparam>
internal sealed class RegistryValueSnapshot<T>
{
    private readonly T _absentValue;
    private readonly Func<object, T> _coerce;
    private readonly RegistryValueKind _defaultKind;
    private readonly Func<AppConfig, State> _load;
    private readonly Func<RegistryValueKind, RegistryValueKind> _normalizeKind;
    private readonly Action<AppConfig, State> _store;

    private readonly string _valueName;
    private readonly object _writeFallback;

    /// <param name="valueName">Registry value name (within a key supplied per call).</param>
    /// <param name="absentValue">
    ///     Value reported by <see cref="ReadCurrent" /> when the
    ///     registry value is missing.
    /// </param>
    /// <param name="writeFallback">
    ///     Written on restore if the persisted value is null
    ///     (possible for reference types in hand-edited or legacy configs).
    /// </param>
    /// <param name="defaultKind">Kind reported when the registry value is missing.</param>
    /// <param name="coerce">Converts the raw registry object to <typeparamref name="T" />.</param>
    /// <param name="normalizeKind">
    ///     Clamps a persisted kind to the kinds that are valid
    ///     for this value, so a corrupted config can't make restore throw.
    /// </param>
    /// <param name="load">Reads the four bound AppConfig properties.</param>
    /// <param name="store">Writes the four bound AppConfig properties.</param>
    /// <summary>Binds one registry value to its durable configuration snapshot fields.</summary>
    public RegistryValueSnapshot(string valueName, T absentValue, T writeFallback,
        RegistryValueKind defaultKind, Func<object, T> coerce,
        Func<RegistryValueKind, RegistryValueKind> normalizeKind,
        Func<AppConfig, State> load, Action<AppConfig, State> store)
    {
        _valueName = valueName;
        _absentValue = absentValue;
        _writeFallback = writeFallback!;
        _defaultKind = defaultKind;
        _coerce = coerce;
        _normalizeKind = normalizeKind;
        _load = load;
        _store = store;
    }

    /// <summary>
    ///     Reads the value's current presence, content and kind from the key.
    ///     A null key reads as absent.
    /// </summary>
    /// <param name="key">Caller-owned registry key to read, or null to represent an absent key.</param>
    /// <returns>An uncaptured state retaining presence and raw registry kind; access/coercion failures propagate.</returns>
    public State ReadCurrent(RegistryKey? key)
    {
        if (key is null)
        {
            return new State(false, false, _absentValue, _defaultKind);
        }

        var sentinel = new object();
        var value = key.GetValue(_valueName, sentinel, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return ReferenceEquals(value, sentinel)
            ? new State(false, false, _absentValue, _defaultKind)
            : new State(false, true, _coerce(value), key.GetValueKind(_valueName));
    }

    /// <summary>
    ///     Persists a previously read state into the bound AppConfig fields,
    ///     marking the snapshot as captured. Does not save the config — callers decide
    ///     when persisting is safe relative to their registry writes.
    /// </summary>
    /// <param name="config">Caller-owned mutable configuration receiving the snapshot.</param>
    /// <param name="current">Previously read registry facts to mark as captured.</param>
    public void Capture(AppConfig config, State current)
    {
        _store(config, current with { Captured = true });
    }

    /// <summary>Checks whether a durable snapshot has been recorded for this policy value.</summary>
    /// <param name="config">Configuration containing the bound snapshot fields.</param>
    /// <returns>True when capture occurred, including when the original registry value was absent.</returns>
    public bool IsCaptured(AppConfig config)
    {
        return _load(config).Captured;
    }

    /// <summary>True when restore should write a value back rather than delete it.</summary>
    /// <param name="config">Configuration containing the bound snapshot fields.</param>
    /// <returns>The recorded presence flag; does not check whether capture occurred.</returns>
    public bool HasValue(AppConfig config)
    {
        return _load(config).Exists;
    }

    /// <summary>
    ///     Puts the registry back the way the snapshot recorded it: rewrite the
    ///     saved value with its original (normalized) kind, or delete the value if the
    ///     snapshot says it was absent.
    /// </summary>
    /// <param name="key">Caller-owned writable registry key; kept open after the operation.</param>
    /// <param name="config">Configuration containing a previously captured snapshot.</param>
    /// <remarks>Caller must check <see cref="IsCaptured" /> first. Registry failures propagate; configuration is not saved or cleared.</remarks>
    public void Restore(RegistryKey key, AppConfig config)
    {
        if (HasValue(config))
        {
            var state = _load(config);
            key.SetValue(_valueName, (object?)state.Value ?? _writeFallback, _normalizeKind(state.Kind));
        }
        else
        {
            key.DeleteValue(_valueName, false);
        }
    }

    /// <summary>
    ///     The four persisted facts about a snapshotted value, mirroring one
    ///     AppConfig field group (SnapshotCaptured / ValueExists / value / ValueKind).
    /// </summary>
    /// <param name="Captured">Whether these facts have been durably captured for restoration.</param>
    /// <param name="Exists">Whether the original registry value existed.</param>
    /// <param name="Value">Coerced original value, or the configured absent-value fallback.</param>
    /// <param name="Kind">Original registry type, or the configured default for an absent value.</param>
    internal readonly record struct State(bool Captured, bool Exists, T Value, RegistryValueKind Kind);
}
