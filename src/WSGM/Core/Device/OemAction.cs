using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>Closed WSGM-owned actions that an OEM control may invoke.</summary>
/// <remarks>
///     There is deliberately no executable, script, shell-command, text-macro, or arbitrary-key action.
///     Keeping the vocabulary here, rather than in the plugin SDK, prevents hardware packages from
///     defining WSGM application policy or turning OEM assignment into a general remapper.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OemAction>))]
public enum OemAction
{
    /// <summary>Do nothing.</summary>
    Disabled,

    /// <summary>Open or close the WSGM overlay.</summary>
    ToggleWsgmOverlay,

    /// <summary>Open or close Steam's native Quick Access Menu.</summary>
    ToggleSteamQuickAccess,

    /// <summary>Open the overlay directly on the Device page.</summary>
    ShowWsgmDevicePage,

    /// <summary>
    ///     Open the quick access sheet on its Open apps strip, or close it when it is up —
    ///     what the taskbar button did before the strip moved into the sheet.
    /// </summary>
    ToggleWsgmTaskbar,

    /// <summary>Switch between Desktop and Game Mode.</summary>
    ToggleDesktopGameMode,

    /// <summary>Show or hide the on-screen keyboard.</summary>
    ToggleOnScreenKeyboard,

    /// <summary>Assign the next device power preset for the current power source.</summary>
    CyclePerformanceProfile,

    /// <summary>Move to the next performance-overlay level.</summary>
    CyclePerformanceOverlayLevel,

    /// <summary>Forward as the current target's first rear control. Rear placement only.</summary>
    VirtualTargetRearButton1,

    /// <summary>Forward as the current target's second rear control. Rear placement only.</summary>
    VirtualTargetRearButton2,

    /// <summary>Invoke Steam's native Home/Overlay button for the active Steam window.</summary>
    ToggleSteamOverlay,

    /// <summary>Hold the right mouse button until the physical OEM gesture is released.</summary>
    MouseSecondaryButton
}
