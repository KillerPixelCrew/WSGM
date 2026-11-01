namespace WSGM.Core;

/// <summary>One explicit activation flag for each process-wide graphics driver owner.</summary>
public sealed class GpuDriverConfig
{
    /// <summary>Whether the installed Intel driver is managed.</summary>
    public bool Intel { get; set; }

    /// <summary>Whether the installed AMD driver is managed.</summary>
    public bool Amd { get; set; }

    /// <summary>Whether the installed NVIDIA driver is managed.</summary>
    public bool Nvidia { get; set; }
}
