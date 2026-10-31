namespace WSGM.Overlay;

/// <summary>Semantic presentation state shared by descriptor-driven overlay rows.</summary>
internal enum DescriptorStatus
{
    None,
    Available,
    Warning,
    Faulted,
    Stale,
    ExternallyOwned,
    Unsupported,
    Progress
}
