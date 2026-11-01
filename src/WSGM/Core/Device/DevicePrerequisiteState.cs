using System.Collections.Generic;
using WSGM.Install;

namespace WSGM.Core;

/// <summary>Whether the detected handheld and its controller prerequisites are available.</summary>
/// <param name="HandheldSupported">Whether LibHandheld supports the detected hardware.</param>
/// <param name="IntegrationEnabled">Whether Device Integration is switched on.</param>
/// <param name="ControllerLibraryInstalled">Whether the virtual controller library is beside WSGM.</param>
/// <param name="HidHideInstalled">Whether the HidHide control device answers.</param>
/// <param name="RequiredComponents">
///     What the exact handheld definition needs, from <see cref="SetupComponents" />.
/// </param>
/// <param name="PawnIoInstalled">Whether the required PawnIO driver is registered.</param>
/// <param name="InpOutInstalled">Whether the required InpOut driver is registered.</param>
public sealed record DevicePrerequisiteState(
    bool HandheldSupported,
    bool IntegrationEnabled,
    bool ControllerLibraryInstalled,
    bool HidHideInstalled,
    IReadOnlyList<SetupComponent> RequiredComponents,
    bool PawnIoInstalled = true,
    bool InpOutInstalled = true);
