using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The exact upstream files needed by one graphics API and process architecture.</summary>
internal sealed record GameToolMode
{
    /// <summary>The proxy filename offered by this mode.</summary>
    public string Proxy { get; init; } = "";

    /// <summary>The API users select.</summary>
    public string Api { get; init; } = "";

    /// <summary>Supported architectures; missing architectures are refused.</summary>
    public string[] Architectures { get; init; } = [];

    /// <summary>Payload-relative source paths to game-relative destinations; {arch} and {bits} are expanded.</summary>
    public Dictionary<string, string> Files { get; init; } = [];
}
