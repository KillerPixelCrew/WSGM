namespace WSGM.Core;

/// <summary>The user's Steam UI sound selection. An empty identity means Steam defaults.</summary>
public sealed class SoundsConfig
{
    /// <summary>The installed folder identity, independent of the pack's display name.</summary>
    public string Selected { get; set; } = string.Empty;
}
