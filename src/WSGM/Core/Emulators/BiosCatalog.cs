namespace WSGM.Core;

internal sealed record BiosCatalog
{
    public BiosSystemDefinition[] Systems { get; init; } = [];
}
