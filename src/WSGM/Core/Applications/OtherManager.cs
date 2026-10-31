using System;
using System.Collections.Generic;
using LibHandheld.Contracts;

namespace WSGM.Core;

/// <summary>A handheld manager WSGM's Full mode replaces, and how it starts itself.</summary>
/// <param name="Id">Stable id, for the log and the records.</param>
/// <param name="Label">Product name shown in setup.</param>
/// <param name="Processes">Its process names, without extension. A logon task running one of them is its autostart.</param>
/// <param name="Services">Its Windows services.</param>
/// <param name="Tasks">Its scheduled tasks, by name in the root folder.</param>
public sealed record OtherManager(
    string Id,
    string Label,
    IReadOnlyList<string> Processes,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Tasks)
{
    /// <summary>Exact MSIX package families whose activation Full mode blocks.</summary>
    public IReadOnlyList<string> PackageFamilies { get; init; } = [];

    /// <summary>One dedicated keyboard hardware identity; the physical machine must also match.</summary>
    public string? DedicatedKeyboardDeviceId { get; init; }

    /// <summary>Exact LibHandheld definition required before this manager's targets are considered.</summary>
    public string? DeviceDefinitionId { get; init; }

    /// <summary>The catalog entry for a handheld maker's own app, as its LibHandheld family describes it.</summary>
    /// <param name="app">The family's app facts.</param>
    /// <returns>An entry carrying the app's identities unchanged.</returns>
    internal static OtherManager From(OemManagerApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new OtherManager(app.Id, app.Name, app.Processes, app.Services, app.Tasks)
        {
            PackageFamilies = app.PackageFamilies, DedicatedKeyboardDeviceId = app.DedicatedKeyboardDeviceId,
            DeviceDefinitionId = app.DefinitionId
        };
    }
}
