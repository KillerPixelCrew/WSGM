using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>A source size WSGM added, kept for explicit reapplication after a driver update.</summary>
public sealed record ScaledResolutionAddition
{
    /// <summary>Exact monitor interface path; never a display number.</summary>
    public string DisplayPath { get; init; } = "";

    /// <summary>Additional physical identity evidence when Windows reported it.</summary>
    public string? EdidIdentity { get; init; }

    /// <summary>Driving adapter's PnP identity; AMD and NVIDIA lists are shared by its outputs.</summary>
    public string AdapterIdentity { get; init; } = "";

    /// <summary>Source width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Source height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Whether the persisted identity and dimensions are usable.</summary>
    [JsonIgnore]
    public bool Valid => !string.IsNullOrWhiteSpace(DisplayPath) && !string.IsNullOrWhiteSpace(AdapterIdentity)
        && Width is >= 100 and <= 15999 && Height is >= 100 and <= 15999;

    /// <inheritdoc />
    public override string ToString() => $"{Width} × {Height}";
}
