using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>One source size in a saved driver list.</summary>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
public readonly record struct ScaledResolutionSize(int Width, int Height)
{
    /// <summary>Whether both dimensions fit the source-mode limits.</summary>
    [JsonIgnore]
    public bool Valid => Width is >= 100 and <= 15999 && Height is >= 100 and <= 15999;
}

/// <summary>The driver list captured before the first explicit edit, including pre-existing sizes.</summary>
public sealed record ScaledResolutionBackup
{
    /// <summary>Exact monitor interface path.</summary>
    public string DisplayPath { get; init; } = "";
    /// <summary>Physical monitor identity when available.</summary>
    public string? EdidIdentity { get; init; }
    /// <summary>Driving adapter's PnP identity.</summary>
    public string AdapterIdentity { get; init; } = "";
    /// <summary>Whether the list belongs to one output rather than the whole adapter.</summary>
    public bool OutputSpecific { get; init; }
    /// <summary>The original recognized source sizes; an empty list is a valid baseline.</summary>
    public List<ScaledResolutionSize> Modes { get; init; } = [];
    /// <summary>Whether the saved identity and every source size are usable.</summary>
    [JsonIgnore]
    public bool Valid => !string.IsNullOrWhiteSpace(DisplayPath) && !string.IsNullOrWhiteSpace(AdapterIdentity)
        && Modes is not null && Modes.All(mode => mode.Valid);
}
