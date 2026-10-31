using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

internal sealed record EmulatorPackageDefinition
{
    public int? SteamGridDbId { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string[] Systems { get; init; } = [];
    public string[] Architectures { get; init; } = [];
    public string[] ExecutableNames { get; init; } = [];
    public string[] LaunchArguments { get; init; } = [];
    public EmulatorDataPolicy DataPolicy { get; init; } = new();
    public string PackageType { get; init; } = "";
    public string[] ValidationArguments { get; init; } = [];
    public int[] ValidationExitCodes { get; init; } = [0];
    public string ValidationOutput { get; init; } = "";
    public Dictionary<string, EmulatorSource> Sources { get; init; } = new(StringComparer.Ordinal);

    public EmulatorDefinition View()
    {
        return new EmulatorDefinition
        {
            Id = Id,
            SteamGridDbId = SteamGridDbId,
            Name = Name,
            Systems = [.. Systems],
            Channels = [.. Sources.Keys],
            DataPolicy = DataPolicy,
            Source = string.Join(" / ", Sources.Values.Select(source => source.BaseUrl).Distinct()),
            Prerequisites = DataPolicy.Prerequisites
        };
    }
}
