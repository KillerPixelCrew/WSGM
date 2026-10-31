namespace WSGM.Overlay;

/// <summary>The single action selected by Back/B after higher-priority UI has been considered.</summary>
internal enum OverlayBackAction
{
    CloseDialog,
    CloseSurface,
    NestedBack,
    FocusRail,
    LeaveNestedPage,

    /// <summary>Return to the Quick access root from another destination's root.</summary>
    ReturnHome,
    CloseOverlay
}
