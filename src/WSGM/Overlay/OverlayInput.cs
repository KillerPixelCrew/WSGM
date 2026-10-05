using System;
using WSGM.Input;

namespace WSGM.Overlay;

/// <summary>The overlay sheet's controller wiring: one navigation owner for the window and its surfaces.</summary>
internal static class OverlayInput
{
    /// <summary>Creates the sheet's navigation over a button source.</summary>
    /// <param name="window">The sheet.</param>
    /// <param name="buttons">The controller buttons.</param>
    /// <param name="back">What Back does once the sheet has nothing nested to close.</param>
    /// <param name="isNintendoLayout">Whether the face buttons use the Nintendo layout.</param>
    /// <returns>The navigation, which the caller disposes with the sheet.</returns>
    internal static GamepadNavigation Create(OverlayWindow window, IUiButtonSource buttons, Action back,
        Func<bool>? isNintendoLayout = null)
    {
        return new GamepadNavigation(buttons, window, back,
            isNintendoLayout,
            () => window.DefaultFocusTarget,
            focused =>
            {
                if (window.IsPowerMenuOpen)
                {
                    window.CloseActiveSurface();
                }
                else if (!window.HasActiveSurface)
                {
                    window.RequestSecondaryAction(focused);
                }
            },
            () =>
            {
                if (!window.NavigateSurfaceTab(false))
                {
                    window.SelectPreviousTab();
                }
            },
            () =>
            {
                if (!window.NavigateSurfaceTab(true))
                {
                    window.SelectNextTab();
                }
            },
            _ =>
            {
                if (!window.HasActiveSurface)
                {
                    window.CycleNextApp();
                }
            },
            direction => !window.HasActiveSurface && window.NavigateWorkspace(direction),
            true, () => window.ActiveSurfaceNavigationRoot);
    }
}
