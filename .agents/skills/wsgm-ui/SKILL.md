---
name: wsgm-ui
description:
  Design, implement or review WSGM workflows in the Overlay, Settings, Steam Quick Access Menu (QAM)
  and routed Steam pages. Use for content hierarchy, collections and editors, reusable controls,
  responsive layout, input/focus, feedback and visual acceptance. Pair with the Core or Steam CEF
  skill for the owning runtime mechanism.
---

# WSGM UI

Make the user's complete task easy to discover and finish. A working backend and a list of fields
are only part of a useful interface. Preserve WSGM's existing visual language and shared owners.

## Start with the task and current screen

Read [UI mechanisms](../../../docs/ui.md), the nearest `AGENTS.md`, and the relevant production view
and current captures. For Steam, also use the
[surface parity guide](../wsgm-steam-cef-toolkit/references/ui-and-overlay-parity.md) and
[element catalog](../wsgm-steam-cef-toolkit/references/reusable-elements.md). The
[research notes](references/design-sources.md) explain the external UX guidance and local skills
used here; they are background, not another implementation pipeline.

Identify the entry point, current scope, primary action and useful result. Trace browse, create,
edit, use and return as relevant, including cancellation, empty/loading/unavailable/error states and
reopening. Resolve these choices briefly alongside implementation; do not turn them into a large
planning artifact. Inspect the whole affected workflow, including dialogs and context menus.

## Respect the actual surfaces and owners

| Surface            | Existing owner and design constraint                                                                                                                                                                                                                           |
| ------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Overlay            | `OverlayWindow`, its `Navigation`/`Surfaces` partials, `OverlayController` and feature `ServiceSubView`s. The fullscreen command deck has a persistent section rail and a controls pane; use nested levels and in-window utilities within that structure.      |
| Steam QAM          | Toolkit typed surfaces, `SteamQuickAccessRowsSurface` and shared settings descriptors provide frequent controls inside Steam's narrow native panels. Preserve Valve content, folding and native navigation. Keep any Reset to Default action last.             |
| Routed Steam pages | `SteamPageSurface`, `SteamPagePatch` and `registerSteamPage` use Steam's router and Back stack. Compose product workflows with native fields and `ui-kit.ts`; WSGM's `page-kit.ts` supplies its shared page frame.                                             |
| Settings           | `SettingsWindow`, page controls and `SettingsViewModel` edit WSGM configuration. Follow its captured-edit/save-merge contract and the exceptions in [product decisions](../../../docs/decisions.md). Live external-state controls belong on Overlay and Steam. |

`ShellSession` creates the shared backend and supplies it through `OverlaySources` and
`SteamUiBackends`; `SteamUiSessionHost` registers Steam projections and commands. Both surfaces must
complete the same task through the same validated actions, availability and results. Transient
navigation, filters and selection can be local to a surface. Keep writes, durable state and
operation lifetime in the existing service. Use [Core](../wsgm-core/SKILL.md) for application
ownership and [Steam CEF](../wsgm-steam-cef-toolkit/SKILL.md) for toolkit integration.

## Organize around content and decisions

- Establish a readable hierarchy: page purpose and scope, primary action, useful content, supporting
  commands. Use specific verbs and units. Keep essential explanations visible; a tooltip may supply
  extra detail but cannot be the only label, error or instruction.
- Collections help people recognize an item before opening it. Show a useful image or text preview,
  name, current/selected status and concise relevant metadata. Keep failures visible on the affected
  item. Choose cards for visual material and native lists for scanning text or values. Add search,
  filtering or pagination when the collection needs them, with selection and scroll retained on
  return.
- Give editors their document, canvas, image or other content as the main workspace. Keep frequent
  tools compact and near that content. Put organization, advanced properties and destructive actions
  in contextual menus, secondary levels or focused dialogs. Progressive disclosure must keep
  frequent actions easy to find; it is not permission to omit functionality.
- Creation starts from the user's source or context, then sensible defaults and any required
  details. Keep names editable, validate near the relevant input and retain the draft on failure.
  File formats, internal IDs, capture machinery and transport steps are not ordinary workflow
  instructions.
- Distinguish an empty library, no matching results, loading, unavailable capability and failed
  work. Give each an appropriate next action. Show pending work and its outcome beside the
  action/content; use the service's actual refusal reason. Do not use a success notice to hide a
  partial failure or automatically repeat an uncertain write. Cancellation must preserve the
  promised files and drafts.
- Use existing undo semantics for recoverable edits. Confirm destructive commands with the affected
  item's name, the actual consequence and a safe cancel action. State whether removing a library
  entry also deletes its files. Avoid confirmation dialogs for ordinary navigation or harmless
  choices.

The existing [Overlay screensaver library](../../../src/WSGM/Overlay/ScreensaversView.cs) and
[Steam screensaver library](../../../external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/screensaver-library.ts)
show preview-led browsing, selected/unavailable status, focused source editing and source-first
creation. Reuse their relevant patterns rather than copying all their fields into another feature.

## Reuse presentation and interaction primitives

For Overlay, use `ActionButton` for commands, actual editors for values, `CollapsibleSection` for
related groups, and `ServiceSubView`/`OverlaySubView` helpers for rows, tabs, levels and status. Use
`Tagged` identities and `OverlayEditors` to reconcile active editors. `EditText`/`RequestText` and
`OverlayFilePicker` provide the in-window text and file workflows, including controller access.
`OverlayPreviewImage` and `OverlayMediaPreview` own bounded previews and playback. Consult the
parity guide for exact files and API choices.

For Steam, resolve and use Steam's React, Focusable and native toggle, dropdown, slider, text,
button and modal controls. Use `renderSteamUiChoice` for compact labelled choices and
`renderSteamUiSelectRow` for readable title/status/detail selections. Compose sections, cards,
galleries, toolbars, empty states and banners with the shared UI kit; include
`steamUiKitStyle(react)` once in each consuming document root. Extend the existing shared primitive
when a reusable behavior is missing. Keep feature labels and decisions in their owner.

Consume the Overlay's `Deck*` and shared `Hc*` palette, typography and focus tokens. Steam uses its
native control language and flat `steam-ui-kit-*` styles. Avoid page-specific copies of controls,
CSS, colors or focus treatments. Preserve distinguishable hover, pressed, disabled, selected and
focused states; communicate meaningful state with text or symbols as well as color.

## Preserve input, state and lifetime

- Keep focus visible and separate from selection. Every actionable item needs an accessible name,
  logical navigation and a usable pointer/touch target. Keep essential actions available without
  hover. Preserve supported keyboard, controller, mouse, touch and pen behavior, including drawing
  and pointer capture in content editors.
- Use the owning navigation and input system. Overlay utilities confine input until dismissed;
  nested Back consumes one level before rail/home navigation. Steam menus, dialogs and pages retain
  native B/Back behavior. Move focus into an opened surface and return it to the invoking control,
  or a logical remaining item after deletion. Feature controls do not acquire their own global
  leases.
- Retain stable semantic IDs and control keys across publication. Preserve drafts, open dropdowns,
  selection and scroll; a state refresh must not replace an active editor or move focus behind a
  dialog. Separate programmatic synchronization from user commits. Reject stale async results using
  the owning cancellation/generation mechanism and update Avalonia state on its dispatcher.
- Load previews on demand with bounded work. Start media on explicit Play, pause behind surfaces and
  release it on leaving. Detach subscriptions and dispose view-owned requests/images when their
  lifetime ends. Leaving a view must follow the service's existing operation lifetime: do not
  silently cancel durable work or leave temporary browse work running indefinitely.

## Fit and inspect the result

Fit the actual Overlay controls pane within its 980 x 640 DIP workspace floor, accounting for the
persistent rail; Settings has a 1024 x 640 floor. Check compact and larger sizes, resizing, content
scaling, populated/empty/error cases and long names/paths. Stack or wrap secondary panels before
reducing readable text or control targets. Keep primary commands and Back reachable, and assign
scrolling deliberately. For large collections, bound mounted rows and previews with existing paging
or virtualization while retaining the user's position and selection.

Steam layouts must fit their actual QAM panel, routed page or modal parent. Make grid/flex children
shrinkable, use border-box sizing and check native dropdown popups as well as closed fields. A
viewport-based minimum width can overflow a nested picker. Check clipped labels, mixed metadata,
horizontal overflow, obscured footers and focus highlights against nearby Valve UI.

Follow the root validation budget. Render changed Avalonia cases with the production preview tools,
open the images personally and correct visible defects. Promote only reviewed named baselines when
that stage is permitted. For authorized Steam work, inspect live screenshots and the entry, focus,
selection, nested Back/cancel and return path. Follow the
[Steam startup preflight](../wsgm-steam-cef-debugging/SKILL.md#before-any-live-cef-connection)
before any CEF connection. A build, DOM text, synthetic component or existing screenshot file does
not prove visual or input acceptance. Record what was actually inspected and any concrete
live/controller/pen or hardware gap, without adding a new approval gate to already authorized work.
