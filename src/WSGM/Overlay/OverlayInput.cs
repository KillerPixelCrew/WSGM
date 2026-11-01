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
        // The browser page's native viewport takes the presses it owns before the sheet sees them.
        BrowserButtons filtered = new(buttons,
            pressed => window.BrowserHost.ConsumeButtons(pressed, isNintendoLayout?.Invoke() == true));
        window.Closed += (_, _) => filtered.Dispose();
        return new GamepadNavigation(filtered, window, back,
            isNintendoLayout,
            () => window.DefaultFocusTarget,
            focused =>
            {
                if (window.Surfaces.IsPowerMenuOpen)
                {
                    window.Surfaces.CloseActive();
                }
                else if (!window.Surfaces.HasActive)
                {
                    window.RequestSecondaryAction(focused);
                }
            },
            () =>
            {
                if (!window.Surfaces.NavigateTab(false))
                {
                    window.SelectPreviousTab();
                }
            },
            () =>
            {
                if (!window.Surfaces.NavigateTab(true))
                {
                    window.SelectNextTab();
                }
            },
            _ =>
            {
                if (!window.Surfaces.HasActive)
                {
                    if (!window.EmulatorManagerHost.SecondaryAction())
                    {
                        window.CycleNextApp();
                    }
                }
            },
            direction => window.Surfaces.HasActive
                ? window.Surfaces.NavigateDirection(direction)
                : window.NavigateWorkspace(direction),
            true, () => window.Surfaces.NavigationRoot)
        {
            // LB/RB switch the Emulators page's views while it is open; the triggers still switch destinations.
            ShoulderTabs = next => !window.Surfaces.HasActive && window.EmulatorManagerHost.SelectAdjacentView(next)
        };
    }
}
