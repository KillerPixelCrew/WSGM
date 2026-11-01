using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>An explicitly enabled installed common-plugin instance.</summary>
public sealed class CommonPluginInstanceConfig
{
    /// <summary>Installed package identity.</summary>
    public string PluginId { get; set; } = "";

    /// <summary>Stable host instance identity within the package.</summary>
    public string InstanceId { get; set; } = "default";

    /// <summary>Whether WSGM may start this trusted installed package.</summary>
    public bool Enabled { get; set; }

    /// <summary>User opt-in to this package's unrestricted Steam frontend.</summary>
    public bool SteamCefEnabled { get; set; }

    /// <summary>Persisted module failure. Only an explicit manual reload clears it.</summary>
    public string? SteamCefFailure { get; set; }

    /// <summary>Transient explicit reload intent captured by Settings, never persisted.</summary>
    [JsonIgnore]
    public bool SteamCefReloadRequested { get; set; }
}
