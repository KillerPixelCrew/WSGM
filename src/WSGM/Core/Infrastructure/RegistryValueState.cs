using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>
///     The four persisted facts about a snapshotted value, mirroring one
///     AppConfig field group (SnapshotCaptured / ValueExists / value / ValueKind).
/// </summary>
/// <param name="Captured">Whether these facts have been durably captured for restoration.</param>
/// <param name="Exists">Whether the original registry value existed.</param>
/// <param name="Value">Coerced original value, or the configured absent-value fallback.</param>
/// <param name="Kind">Original registry type, or the configured default for an absent value.</param>
internal readonly record struct RegistryValueState<T>(bool Captured, bool Exists, T Value, RegistryValueKind Kind);
