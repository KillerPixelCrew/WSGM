using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One compatible installation and the cores the shared launch policy permits.</summary>
/// <param name="Id">The installation identity.</param>
/// <param name="Label">The installation's name, version and ownership.</param>
/// <param name="RequiresCore">Whether this installation launches through a core.</param>
/// <param name="DefaultCoreId">The first compatible core, or empty.</param>
/// <param name="Cores">The already ordered and labelled compatible cores.</param>
public sealed record RomEmulatorChoice(
    string Id,
    string Label,
    bool RequiresCore,
    string DefaultCoreId,
    IReadOnlyList<RomEmulatorCoreChoice> Cores);
