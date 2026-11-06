using LibGPUDriverInteract;

namespace WSGM.Shell;

/// <summary>Detached update state shared by Overlay and Steam; every write belongs to the session service.</summary>
internal sealed record GpuDriverUpdateState(GpuInstalledDriver Adapter, GpuDriverUpdateStage Stage, string Detail,
    GpuDriverPackage? Package = null, int Progress = 0)
{
    internal const string EmptyText = "No supported Intel, AMD or NVIDIA adapter was found.";
    internal bool Busy => CanCancel || Stage == GpuDriverUpdateStage.Installing;
    internal bool CanCancel => Stage is GpuDriverUpdateStage.Checking or GpuDriverUpdateStage.Downloading or GpuDriverUpdateStage.Verifying;
    internal bool ShowsProgress => Stage is GpuDriverUpdateStage.Downloading or GpuDriverUpdateStage.Verifying or GpuDriverUpdateStage.Installing;
    internal bool Indeterminate => Stage != GpuDriverUpdateStage.Downloading;
    internal bool CanInstall => Package is not null && Stage is GpuDriverUpdateStage.Available or GpuDriverUpdateStage.Failed;
    internal string StageText => Stage switch
    {
        GpuDriverUpdateStage.NotChecked => "Not checked",
        GpuDriverUpdateStage.UpToDate => "Up to date",
        GpuDriverUpdateStage.RestartRequired => "Restart required",
        _ => Stage.ToString()
    };
    internal string DisplayText => Stage == GpuDriverUpdateStage.Downloading ? $"Downloading… {Progress}%" : Detail;
    internal string StatusText => DisplayText.StartsWith(StageText, System.StringComparison.OrdinalIgnoreCase)
        ? DisplayText : StageText + ": " + DisplayText;
}
