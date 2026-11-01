using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed record EmulatorPageState(
    EmulatorSnapshot Emulators,
    IReadOnlyList<RomSystemProfile> RomSystems,
    IReadOnlyList<RomSystemEmulatorChoices> Choices,
    IReadOnlyList<EmulatorDependencyCount> Dependencies,
    string Architecture)
{
    public IReadOnlyList<RomSystemProfile> RomSourceSystems => [RomProfiles.Automatic, .. RomSystems];
    public EmulatorBiosState Bios { get; init; } = new("", false, []);
    public EmulatorListItem[] Installed { get; init; } = [];
    public EmulatorCoreStatus[] CoreStatus { get; init; } = [];
    public string ShortcutStatus { get; init; } = "";

    /// <summary>The launch modes an emulator shortcut offers, listed alike by the Steam page and the overlay.</summary>
    public IReadOnlyList<EmulatorLaunchMode> LaunchModes { get; init; } = EmulatorLaunchMode.Offered;

    /// <summary>Every supported emulator as Get emulators lists it, sorted by name.</summary>
    public EmulatorCatalogItem[] Catalog { get; init; } = [];

    /// <summary>The console families the catalogue covers, in filter order.</summary>
    public string[] Families { get; init; } = [];

    /// <summary>The updates Update all would apply, in the order it applies them.</summary>
    public EmulatorPendingUpdate[] Updates { get; init; } = [];

    /// <summary>"2 updates ready: RetroArch 1.22.0 and Cemu 2.7", or empty when none is.</summary>
    public string UpdatesSummary { get; init; } = "";

    /// <summary>The last Update all run, or null when none ran since another operation started.</summary>
    public EmulatorUpdateAllState? UpdateAll { get; init; }

    /// <summary>Every system with its library usage, in the importer's order.</summary>
    public EmulatorSystemRow[] Systems { get; init; } = [];

    /// <summary>The BIOS folder's checklist and its found count.</summary>
    public EmulatorBiosSummary BiosSummary { get; init; } = new(0, 0, "", []);
}
