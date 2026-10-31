using System.Collections.Generic;
using System.Linq;
using LibGPUDriverInteract;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed record ScaledResolutionState(
    IReadOnlyList<ScaledResolutionDisplay> Displays,
    string? SelectedPath,
    GpuScaledResolutionCatalog? Catalog,
    GpuScaledResolution? Native,
    IReadOnlyList<ScaledResolutionAddition> Remembered,
    IReadOnlyList<ScaledResolutionAddition> Missing,
    string Detail)
{
    internal IReadOnlyList<GpuScaledResolution> Available { get; init; } = [];
    internal ScaledResolutionBackup? Original { get; init; }

    /// <summary>Whether edits update an adapter-wide list activated by restarting Windows or its driver.</summary>
    internal bool RequiresRestart => Catalog is { Supported: true, OutputSpecific: false };

    /// <summary>Activation guidance shown before editing on both settings surfaces.</summary>
    internal string ActivationNotice => RequiresRestart
        ? "Restart Windows after changing this resolution list. AMD and NVIDIA changes are not available until Windows or the graphics driver is restarted."
        : "";

    internal IReadOnlyList<GpuScaledResolution> Resolutions => (Catalog?.Modes ?? [])
        .Distinct().OrderByDescending(mode => mode.Width).ThenByDescending(mode => mode.Height).ToArray();

    internal string ResolutionLabel(GpuScaledResolution mode) => $"{mode.Width} × {mode.Height}"
        + (mode == Native ? " · native" : "");
}
