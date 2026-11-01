using System.Collections.Generic;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>One plugin action in a session automation list.</summary>
public sealed class PluginActionStep
{
    /// <summary>The shortest accepted <see cref="TimeoutSeconds" />.</summary>
    public const int MinimumTimeoutSeconds = 1;

    /// <summary>The longest accepted <see cref="TimeoutSeconds" />.</summary>
    public const int MaximumTimeoutSeconds = 120;

    /// <summary>Configured provider identity.</summary>
    public PluginInstanceIdentity? Plugin { get; set; }

    /// <summary>Stable action ID within the provider.</summary>
    public string? ActionId { get; set; }

    /// <summary>Explicit action arguments. Missing values use admitted provider defaults.</summary>
    public Dictionary<string, PluginValue> Arguments { get; set; } = [];

    /// <summary>Deadline for this step alone, from 1 to 120 seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
