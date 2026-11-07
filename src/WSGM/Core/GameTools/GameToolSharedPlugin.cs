namespace WSGM.Core;

/// <summary>Durable shared-plugin ownership and the games retaining it, including disabled games.</summary>
internal sealed record GameToolSharedPlugin
{
    public string Directory { get; init; } = "";
    public uint[] Consumers { get; init; } = [];
    public GameToolManifest? Manifest { get; init; }
    public bool External { get; init; }
}
