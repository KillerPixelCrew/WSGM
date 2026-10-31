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
}
