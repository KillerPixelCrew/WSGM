namespace WSGM.Core;

/// <summary>An upstream package and file set used by a shared Special K plugin.</summary>
internal sealed record GameToolSharedDependency
{
    public string SourceToolId { get; init; } = "";
    public string RelativeDirectory { get; init; } = "";
    public GameToolMode Mode { get; init; } = new();
}
