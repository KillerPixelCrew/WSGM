namespace WSGM.Core;

internal sealed record BiosFileDefinition
{
    public string Path { get; init; } = "";
    public string[] Md5 { get; init; } = [];
    public bool Optional { get; init; }
    public bool Directory { get; init; }
}
