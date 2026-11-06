using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The exact upstream files needed by one graphics API and process architecture.</summary>
internal sealed record GameToolMode
{
    /// <summary>The proxy filename offered by this mode.</summary>
    public string Proxy { get; init; } = "";

    /// <summary>The API users select.</summary>
    public string[] Apis { get; init; } = [];

    /// <summary>Guidance for choosing this mode.</summary>
    public string Hint { get; init; } = "";

    /// <summary>Whether this mode can be suggested without another loader.</summary>
    public bool Suggest { get; init; } = true;

    /// <summary>A concise display label for the supported APIs.</summary>
    internal string Api => Apis.Length > 0 && System.Linq.Enumerable.All(Apis, api => api.StartsWith("Direct3D ", System.StringComparison.Ordinal))
        ? "Direct3D " + string.Join(" / ", System.Linq.Enumerable.Select(Apis, api => api[9..]))
        : string.Join(" / ", Apis);

    /// <summary>Supported architectures; missing architectures are refused.</summary>
    public string[] Architectures { get; init; } = [];

    /// <summary>Payload-relative source paths to game-relative destinations; {arch} and {bits} are expanded.</summary>
    public Dictionary<string, string> Files { get; init; } = [];
}
