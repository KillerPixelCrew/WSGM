using WSGM.Core;
using OemControlPlacement = LibHandheld.Contracts.OemControlPlacement;

namespace WSGM.Shell;

/// <summary>WSGM-owned assignment and runtime-availability policy for physical OEM controls.</summary>
internal static class OemActionRules
{
    internal static bool IsAssignable(OemAction action, OemControlPlacement placement)
    {
        return !IsVirtualTargetButton(action) || placement is OemControlPlacement.Rear;
    }

    internal static bool IsVirtualTargetButton(OemAction action)
    {
        return action
            is OemAction.VirtualTargetRearButton1
            or OemAction.VirtualTargetRearButton2;
    }

    internal static bool IsAvailable(OemAction action, bool targetHasRearButtons)
    {
        return !IsVirtualTargetButton(action) || targetHasRearButtons;
    }
}
