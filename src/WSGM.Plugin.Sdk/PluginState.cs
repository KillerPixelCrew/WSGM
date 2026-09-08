using System;

namespace WSGM.Plugin.Sdk;

/// <summary>One bounded primitive value for common settings and observable state.</summary>
/// <param name="Boolean">Boolean value, exclusive with Number and Text.</param>
/// <param name="Number">Finite numeric value, exclusive with Boolean and Text.</param>
/// <param name="Text">Plain text value, exclusive with Boolean and Number.</param>
public readonly record struct PluginValue(bool? Boolean = null, double? Number = null, string? Text = null)
{
    /// <summary>Whether exactly one bounded value is present.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => (Boolean.HasValue ? 1 : 0) + (Number.HasValue ? 1 : 0) + (Text is null ? 0 : 1) == 1
        && (!Number.HasValue || double.IsFinite(Number.Value)) && (Text is null || Text.Length <= 4096);
}

/// <summary>Origin of an observation. No origin authorizes persisting a value as user configuration.</summary>
public enum PluginStateOrigin
{
    /// <summary>Initialization or a plugin-supplied default.</summary>
    Initialization,
    /// <summary>Observation after applying host-supplied configuration.</summary>
    Configuration,
    /// <summary>Observation following a requested action.</summary>
    Action,
    /// <summary>Independent external-state readback.</summary>
    HardwareReadback,
}

/// <summary>One named effective-state observation, never a request to change saved configuration.</summary>
/// <param name="Instance">Admitted instance identity.</param>
/// <param name="Generation">Current host lifecycle generation.</param>
/// <param name="Sequence">Strictly increasing publication sequence within this instance generation.</param>
/// <param name="Key">Stable lowercase state key.</param>
/// <param name="Value">Observed bounded primitive value.</param>
/// <param name="Origin">What produced this observation.</param>
/// <param name="ConfigurationRevision">Related host configuration revision, or null for independent observations.</param>
/// <param name="OperationId">Related host action identity, or null.</param>
public sealed record PluginStatePublication(PluginInstanceIdentity Instance, long Generation, long Sequence,
    string Key, PluginValue Value, PluginStateOrigin Origin, long? ConfigurationRevision = null, Guid? OperationId = null);
