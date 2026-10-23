# UI design sources

Reviewed online on 2026-10-23. These sources inform the skill's UX choices; WSGM's current source,
surface conventions and root `AGENTS.md` govern implementation. Apply Fluent and WAI interaction
principles through WSGM's existing Avalonia and Steam controls. Valve's game-compatibility criteria
inform handheld usability; they are not a claim that WSGM is a Steam Deck certified game.

## Primary guidance

| Source                                                                                                                                         | What it informs here                                                                                                                                                                                                           |
| ---------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Microsoft: [Design for productivity in WinUI apps](https://learn.microsoft.com/en-us/windows/apps/get-started/line-of-business/design-for-lob) | Use established controls and theme resources. Improve grouping and disclose secondary options before making controls smaller. Adapt details panes and command placement while preserving readability, focus and input targets. |
| Microsoft: [Navigation design basics](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics)                          | Familiar, clearly labelled routes; shallow useful task flows; predictable Back that dismisses transient UI and returns to context.                                                                                             |
| Microsoft: [Responsive design techniques](https://learn.microsoft.com/en-us/windows/apps/design/layout/responsive-design)                      | Reposition, resize and reflow content according to available space and input. WSGM's controls-pane and modal-parent bounds determine the actual layout.                                                                        |
| Microsoft: [Dialog controls](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs)                   | Reserve blocking dialogs for decisions that need them. Use specific action labels, a safe exit and contextual inline validation.                                                                                               |
| W3C WAI: [Developing a Keyboard Interface](https://www.w3.org/WAI/ARIA/apg/practices/keyboard-interface/)                                      | Visible, predictable focus; selection distinct from focus; familiar navigation within and between controls. Retain native controls' keyboard behavior.                                                                         |
| W3C WAI: [Modal Dialog Pattern](https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/)                                                        | Initial focus, confined modal navigation, accessible dialog names and return focus. Apply through the existing modal hosts and Steam navigation rather than adding a separate focus system.                                    |
| Valve: [Getting ready for Steam Deck and Steam Machine](https://partner.steamgames.com/doc/steamhardware/recommendations)                      | Reach the full workflow with a controller and provide controller-accessible text entry. Consider small displays and different aspect ratios. Use WSGM's existing input/text owners.                                            |
| Valve: [Steam hardware compatibility review](https://partner.steamgames.com/doc/steamhardware/compat)                                          | Readable text at the actual handheld resolution, accurate input prompts and complete controller navigation. Pixel criteria for games do not replace WSGM's DIP floors or a real display/input review.                          |

## Existing skills inspected

These are comparisons made during authoring, not additional runtime dependencies or mandatory
workflows for using this skill.

| Inspected skill or reference                                                                                                                                                                                                         | Applied judgment                                                                                                                                                                                                                    |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [WSGM](../../wsgm/SKILL.md) and [WSGM Core](../../wsgm-core/SKILL.md)                                                                                                                                                                | Keep policy and lifetime in their current owners; share backend state/actions while letting surfaces own transient presentation.                                                                                                    |
| [Steam CEF toolkit](../../wsgm-steam-cef-toolkit/SKILL.md), its [parity guide](../../wsgm-steam-cef-toolkit/references/ui-and-overlay-parity.md) and [element catalog](../../wsgm-steam-cef-toolkit/references/reusable-elements.md) | Concrete native controls, Overlay equivalents, stable editor identity, task parity and personally inspected live Steam screenshots.                                                                                                 |
| [Steam CEF debugging](../../wsgm-steam-cef-debugging/SKILL.md)                                                                                                                                                                       | Use the current-run startup-log preflight before even read-only live CEF discovery.                                                                                                                                                 |
| Installed `visualize` 1.0.55, `openai-bundled/visualize/1.0.55/skills/visualize/SKILL.md`                                                                                                                                            | Keep composition focused, essential content visible, labels accessible and controls responsive. Its inline HTML utilities, chart styling, fixed host widths and fragment runtime do not apply to WSGM.                              |
| Installed `sites:sites` 1.1.1, `openai-curated-remote/sites/1.1.1/skills/sites/SKILL.md`, and `references/design/presentation-led.md`                                                                                                | Build a complete task progression, preserve established branding, and make content concrete with useful previews. Presentation-site narrative, marketing layouts and hosting workflows do not belong in these application surfaces. |

## Production patterns checked

- [docs/ui.md](../../../../docs/ui.md) and
  [Overlay/input ownership](../../../../docs/overlay-and-input.md) define layout, navigation, focus,
  preview and lifetime behavior. The parity guide above remains the shared control map.
- `ShellSession`, `OverlaySources`, `SteamUiBackends` and `SteamUiSessionHost` supply one shared
  service to both frontends; `ServiceSubView` retains navigation/editor context through refreshes.
- [Overlay ScreensaversView](../../../../src/WSGM/Overlay/ScreensaversView.cs) and
  [Steam screensaver-library.ts](../../../../external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/screensaver-library.ts)
  demonstrate useful covers, selected/unavailable status, source-first creation and focused editing.
  They are reusable workflow examples, not proof of a new feature's visual or hardware acceptance.
