using System;
using System.Collections.Generic;

namespace WSGM.Core;

internal sealed record EmulatorSource
{
    public string Provider { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string Repository { get; init; } = "";
    public string Manifest { get; init; } = "";
    public Dictionary<string, string> Repositories { get; init; } = new(StringComparer.Ordinal);
    public bool BodyAssets { get; init; }
    public string ChecksumFile { get; init; } = "";
    public string AssetPattern { get; init; } = "";
    public Dictionary<string, string> AssetArchitectureNames { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> AssetVariants { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PlatformNames { get; init; } = new(StringComparer.Ordinal);
    public string PackageName { get; init; } = "";
    public string PackagePattern { get; init; } = "";
    public string CorePath { get; init; } = "";
    public string AssetPath { get; init; } = "";
}
