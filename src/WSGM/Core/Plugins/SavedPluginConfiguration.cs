using System.Collections.Generic;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>Isolated saved preference snapshot for optimistic instance-level edits.</summary>
/// <param name="Revision">Nonnegative intent revision; zero represents no saved preferences.</param>
/// <param name="Values">Read-only copy of explicitly saved values, excluding declaration defaults and readback.</param>
internal sealed record SavedPluginConfiguration(long Revision, IReadOnlyDictionary<string, PluginValue> Values);
