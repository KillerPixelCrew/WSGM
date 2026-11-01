namespace WSGM.Core;

internal sealed record BiosSystemDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Any { get; init; }
    public bool Optional { get; init; }
    public BiosFileDefinition[] Files { get; init; } = [];
}
