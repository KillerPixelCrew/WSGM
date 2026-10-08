# Shared UI and Big Picture / Overlay parity

Use this guide when adding or changing a capability or workflow in Steam Big Picture. **Always
provide the same capability or workflow in WSGM's Overlay.** Both presentations must use the same
service, state and semantic commands, including availability, validation, progress and refusal
reasons. A Steam-only entry point leaves the feature unfinished unless the maintainer explicitly
scopes an exception.

Parity means the user can complete the same task. Each surface should use its own shared controls,
styles and navigation. A Steam card grid can correspond to Overlay preview cards and nested rows;
neither needs to copy the other's layout. Each surface can own transient navigation, search and
selection state; authoritative settings and operations still belong to the shared service. For
example, artwork browsers create separate browse contexts through the same artwork owner.

Read the [toolkit element catalog](reusable-elements.md) for the complete Steam API and helper
index, [architecture](architecture.md) for layer ownership, and the
[change playbook](change-playbook.md) for implementation order. This guide maps those building
blocks to WSGM's Avalonia UI.

## Choose the owner before the control

| Concern                                            | Owner and example                                                                                                                                                                                                                                                                                                                                                            |
| -------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Service lifetime and shared dependencies           | [`ShellSession`](../../../../src/WSGM/Shell/ShellSession.cs) creates the service, then supplies it through `SteamUiBackends` and `OverlaySources`. A view opening must not create another hardware poller, importer or configuration store.                                                                                                                                  |
| Validation, writes, cancellation and durable state | The existing session service or feature manager. [`NativeQamBrightnessService`](../../../../src/WSGM/Shell/NativeQamBrightnessService.cs) owns backlight operations; [`GameLibraryService`](../../../../src/WSGM/Shell/GameLibraryService.cs) owns the import workflow.                                                                                                      |
| Steam publication and command registration         | [`SteamUiSessionHost`](../../../../src/WSGM/Shell/SteamUiSessionHost.cs), the owning `Steam*Surface.Module`, and its backend interface. Keep the registered command vocabulary closed.                                                                                                                                                                                       |
| Overlay state and commands                         | [`OverlaySources`](../../../../src/WSGM/Overlay/OverlaySources.cs), [`OverlayToolSessions`](../../../../src/WSGM/Shell/OverlayToolSessions.cs), and the owning view. `IChangeSource.Changed` announces state changes; views render snapshots and invoke the existing service.                                                                                                |
| Reusable Steam presentation                        | Toolkit `Surfaces/` and `SteamUiAssets/Source/`; choose an existing native surface or row before adding a page. Reusable Steam behavior belongs in the toolkit submodule.                                                                                                                                                                                                    |
| Reusable Avalonia presentation                     | [`Controls`](../../../../src/WSGM/Controls), [`Themes`](../../../../src/WSGM/Themes), and shared Overlay row helpers. Controls expose properties/events and user intent; they do not look up application singletons or own policy.                                                                                                                                           |
| WSGM configuration editing                         | [`Settings`](../../../../src/WSGM/Settings) and the shared setting definitions in [`WsgmSharedSettings`](../../../../src/WSGM/Core/WsgmSharedSettings.cs). Settings edits WSGM-owned preferences, integration switches and profiles. Live external-state controls such as current Windows brightness, audio, radios and storage operations belong on Steam/Overlay surfaces. |

Do not add a Settings page as the counterpart to a Big Picture runtime feature. Add its Overlay
entry point, using the existing section registry in
[`OverlayWindow.Navigation`](../../../../src/WSGM/Overlay/OverlayWindow.Navigation.cs). A WSGM
preference that enables the feature can still belong in Settings; distinguish that preference from
the external state the feature reads or changes.

## Pick a reusable element

| User intent                                   | Steam Big Picture                                                                                                  | Overlay / Avalonia                                                                                                                                                                                                                                                                                                                       |
| --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Run one command                               | Native Steam button or toolkit `renderSteamUiActions`                                                              | [`ActionButton`](../../../../src/WSGM/Controls/ActionButton.cs); in a sub-view use `Row`, `PrimaryRow` or `DangerRow`. Supply a title and useful description; an empty description takes no space.                                                                                                                                       |
| Edit a boolean, choice or bounded value       | Existing native surface/row; for host settings, the toolkit settings renderer's `boolean`, `choice` or `range` row | [`DeviceControlRows`](../../../../src/WSGM/Overlay/DeviceControlRows.cs), [`DeviceSliderRow`](../../../../src/WSGM/Overlay/DeviceSliderRow.cs), or `OverlaySubView.ChoiceRow` for service pages. A value editor should remain an editor rather than a command that cycles values.                                                        |
| Render a capability or profile descriptor     | Existing device/performance row projection and its action generation                                               | [`DescriptorControlView`](../../../../src/WSGM/Overlay/DescriptorControlView.cs), [`CapabilityRowRenderer`](../../../../src/WSGM/Overlay/CapabilityRowRenderer.cs), and [`ProfileOverrideMarker`](../../../../src/WSGM/Controls/ProfileOverrideMarker.cs). Reuse descriptor identity and validation when placing the same setting twice. |
| Group or fold related controls                | `renderSteamUiHeader` / `renderSteamUiGroup`                                                                       | [`CollapsibleSection`](../../../../src/WSGM/Controls/CollapsibleSection.cs), with `Summary` and optional independent action such as a pin. Both implementations retain the mounted body while folded and keep hidden children out of navigation.                                                                                         |
| Switch tabs or descend into details           | `renderSteamUiTabbedPage`, `renderSteamUiLevel` and `renderSteamUiDetail`                                          | [`ServiceSubView`](../../../../src/WSGM/Overlay/ServiceSubView.cs), `Navigate` / Back, and `ToolTabs` from [`OverlaySubView.Tools`](../../../../src/WSGM/Overlay/OverlaySubView.Tools.cs). Use the window's existing destinations and modal host.                                                                                        |
| Browse images or previews                     | `renderSteamUiGrid`, `renderSteamUiCard`, `renderSteamUiGallery`, `renderSteamUiVideo`                             | `PreviewCard`, [`OverlayPreviewImage`](../../../../src/WSGM/Overlay/OverlayPreviewImage.cs) and [`OverlayMediaPreview`](../../../../src/WSGM/Overlay/OverlayMediaPreview.cs). Keep bounded image loading and explicit media playback/release.                                                                                            |
| Explain loading, an empty result or a refusal | `renderSteamUiEmpty` / `renderSteamUiBanner`; WSGM pages use `wsgmPageFrame`                                       | `ServiceSubView.AddStatus`, captions and `Run` / `ConfirmCommand`. Distinguish unavailable, working, no results and failed; preserve the service's reason.                                                                                                                                                                               |
| Enter text or choose a file/folder            | Toolkit text/modal controls and file-picker surface                                                                | `OverlaySubView.EditText` / [`OverlayWindow.RequestText`](../../../../src/WSGM/Overlay/OverlayWindow.Surfaces.cs) and [`OverlayFilePicker`](../../../../src/WSGM/Overlay/OverlayFilePicker.cs) through the owning view. Use the in-window controller flow; a normal `TextBox` alone does not supply it.                                  |

The `OverlaySubView` helpers are `private protected` implementation helpers for WSGM views, not a
public plugin API. Likewise, toolkit TypeScript helpers are composed into the injected asset's
shared scope; they are not separately imported npm components. Keep additions in the existing helper
family when the behavior is reusable, and keep feature-specific labels and decisions in the feature.

## Styles and focus

Avalonia's source of truth is [`Palette.axaml`](../../../../src/WSGM/Themes/Palette.axaml),
[`CommandDeck.axaml`](../../../../src/WSGM/Themes/CommandDeck.axaml) and
[`Typography.axaml`](../../../../src/WSGM/Themes/Typography.axaml). `App.axaml` includes themes;
pages consume them.

- Use the `Hc*` palette for shared application presentation and `Deck*` tokens for the Overlay
  command deck. Examples include `DeckGroupBrush`, `DeckGroupBorderBrush`, `DeckSurfaceBrush`,
  `DeckTextBrush`, `DeckSecondaryBrush`, `DeckControlHeight`, `DeckActionHeight`, `DeckGroupPadding`
  and `DeckSectionGap`. Add a semantic token to its owning theme when necessary instead of copying
  colors or dimensions into a page.
- Use `DynamicResource` for `HcAccentBrush`, `HcOnAccentBrush` and `HcOnAccentCaptionBrush`; stable
  tokens use `StaticResource`. Reuse `setting-title`, `caption`, `eyebrow`, `deck-action`, `primary`
  and `danger` styles where appropriate. An `ActionButton` adds `deck-action` itself.
- Keep one visible two-pixel focus border and disable the framework focus adorner where that border
  supplies focus. Preserve distinct normal, hover, pressed, disabled, selected and `:focus-visible`
  states, including changed accents and high contrast. Selection remains visible when focus leaves a
  section or tab.
- Use [`Icons`](../../../../src/WSGM/Controls/Icons.cs) stroke geometry with no fill. Give controls
  meaningful automation names and preserve keyboard, controller and touch operation.

Steam presentation uses its resolved native fields first, then the shared toolkit
[`ui-kit.ts`](../../../../external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/ui-kit.ts)
for structures Steam does not supply. The kit owns its `steam-ui-kit-*` stylesheet and roots render
`steamUiKitStyle` in the document they inhabit. It uses Steam's React and Focusable components and
the `gpfocus` state. Extend that shared stylesheet/helper when necessary; do not introduce another
page-specific focus system, hard-code a hashed Steam class or copy Avalonia's palette into Steam.

## Keep state refresh separate from user intent

### Contain native fields and selectable rows

Use `renderSteamUiChoice` in a narrow box, toolbar or table cell. Steam's full DropDownField
includes its own label, spacing and a fixed minimum-width control; placing it beside another label
repeats the text and can overflow the column. The helper uses the native bare dropdown when
available and below layout for its field fallback. Give it an accessible label and hide only the
duplicate visible label in a table. Use `renderSteamUiSelectRow` for title/status/detail buttons,
rather than appending several spans that run together inside Steam's native button.

Grid/flex children must be shrinkable, percentage-width controls must use border-box sizing, and
pickers must fit their actual modal parent. Never force a picker body's minimum width from viewport
width alone. Verify long names/paths and narrow columns in the running Steam client: compare control
and containing-panel bounds, check the modal footer, and open the dropdown to check its popup.
Compilation, React stand-ins and Overlay captures do not establish Steam layout acceptance.

Readback, controller focus and editing must coexist. Preserve stable semantic row IDs and keys
across refreshes; avoid replacing the focused control during an active edit. In Overlay service
views, use `Tagged` keys with `ServiceSubView` reconciliation and the refreshable editors in
[`OverlayEditors`](../../../../src/WSGM/Overlay/OverlayEditors.cs). `OverlayChoice<T>` retains an
open dropdown while state refreshes. Device dropdowns use
[`ComboCommit`](../../../../src/WSGM/Controls/ComboCommit.cs): browsing an open list does not write,
closing commits the chosen value, and programmatic synchronization does not write.

Hardware editors must revalidate current capability identity and generation before submission. Use
the existing descriptor/row implementation so a replaced device cannot receive an old draft. An
unavailable reading is not a valid zero or an instruction to change the device. In the Steam module
contract, `null` state means publish nothing.

Use each workflow's existing commit boundary. A theme color editor can debounce preview changes; a
device operation that explicitly requires Apply must keep that requirement. Do not apply a generic
timer to every slider or color control.

For navigation, preserve the Overlay's nested Back path, stable section selection and confined modal
focus. A refresh must not move focus behind an open utility surface. `ServiceSubView` defers its
current-level refresh while that surface is open. Keep folded bodies mounted for state
reconciliation but exclude their controls from focus. Full behavior and input ownership are in
[overlay and input](../../../../docs/overlay-and-input.md).

## Two implementation patterns to follow

### One live value: brightness

`ShellSession` creates one `NativeQamBrightnessService`. `SteamUiSessionHost` registers
[`SteamBrightnessSurface.Module`](../../../../external/steam-ui-toolkit/src/SteamUiToolkit/Surfaces/SteamBrightnessSurface.cs)
with that service's `ReadAsync` and `ISteamBrightnessBackend` implementation. The Overlay receives
the same instance and
[`DisplayBrightnessView`](../../../../src/WSGM/Overlay/DisplayBrightnessView.cs) subscribes to
`Changed`, reads `Current` and calls `SetBrightnessAsync`.

The service serializes writes and publishes a monotonic observation revision. An accepted write
records the requested level; later polling can correct it, and success does not wait for readback.
The Overlay's synchronization guard prevents published state from becoming a new user write and
shows a refusal beside the editor. Add another projection of this value through that service, not
through another Windows call in the view.

### A complete workflow: Library Importer

[`SteamLibraryImportSurface`](../../../../src/WSGM/Shell/SteamLibraryImportSurface.cs) registers
commands and publishes the `GameLibraryService` state to
[`library-import.ts`](../../../../src/WSGM/Core/SteamUiAssets/Source/library-import.ts).
[`GameLibraryView`](../../../../src/WSGM/Overlay/GameLibraryView.cs) and
[`GameLibraryView.Tools`](../../../../src/WSGM/Overlay/GameLibraryView.Tools.cs) consume
`IGameLibraryOverlaySource`, which extends the same `IGameLibraryBackend` with state/detail reads
and change notifications.

Follow this pair for a workflow with source selection, scan, review, details, artwork, apply,
progress and cancellation. The service owns the operation: leaving the Overlay does not cancel a
scan or apply. Reopening renders the current state, and Stop invokes the service's cancellation
command. Short-lived browsing work can have a different lifetime; the browse-session interfaces
explicitly expose cancellation and disposal. State the chosen lifetime in the feature documentation.

This existing Overlay fragment shows the intended composition inside a `ServiceSubView`; reuse its
helpers and the service method, then add the corresponding Steam command/row:

```csharp
stack.Children.Add(Tagged(Row("Scan for games", "Look again. Nothing is written until you apply",
    Icons.Restart, () => Run(_service.ScanAsync)), "scan"));
```

For a store/detail page, follow the pair
[`themes.ts`](../../../../src/WSGM/Core/SteamUiAssets/Source/themes.ts) and
[`ThemesView`](../../../../src/WSGM/Overlay/ThemesView.cs). The Steam page composes native settings
rows and toolkit cards/galleries. Its shared
[`page-kit.ts`](../../../../src/WSGM/Core/SteamUiAssets/Source/page-kit.ts) maps `activeTab`,
`error`/`notice`, `setTab` and `dismiss`. The Overlay presents the same browse-session commands with
tabs, preview cards and nested detail levels. Keep labels, busy state, available actions and
operation results consistent across both.

## Small feature handoff template

Fill in this list before implementing. Keep it with the change description so another maintainer can
find the owners without reading every file.

```text
Capability/workflow:
Shared service, state type and lifetime:
Semantic command(s), validation and commit boundary:
Steam module/surface, entry point and reused elements:
Overlay source/view, entry point and reused elements:
Availability, loading, empty, error/refusal and retry behavior:
Back, focus, cancellation and close/reopen behavior:
Documentation and offline/manual evidence:
```

Completion requires both paths to cover the same outcomes:

- Both entry points reach the capability or the complete workflow, including details and follow-up
  actions. A link that sends Overlay users to Steam is not an implemented Overlay counterpart.
- Both read the same service state and invoke its validated semantic operations. Neither view adds
  another backend, persistence path, polling loop or independent interpretation of availability.
- Busy, unavailable, empty, success and refusal states are legible; actions remain consistent with
  the service's current state. Stale selections and callbacks cannot write to a replaced entity.
- Controller, keyboard and pointer navigation retain focus through refreshes, folds, dialogs and
  return from details. Verify the Overlay at its 980 × 640 DIP floor and Steam in its real target
  surface when an attended pass is authorized.
- Subscriptions, transient requests, images and media follow the owning lifetime. Closing a view
  neither leaks its work nor silently cancels a service-owned durable operation.
- Update the owning docs and reuse catalogs when adding an element or changing its contract. Record
  offline checks separately from actual UI/device evidence, following root `AGENTS.md` manual-first
  test timing.

Before any live CEF debugger, CDP, MCP or target-discovery connection, follow the
[startup prerequisite](../SKILL.md#before-any-live-cef-connection): current-run Steam logs must
prove both Steam and Big Picture have fully started. An endpoint, window or elapsed delay is
insufficient; inconclusive logs mean remain offline. Early attachment can hang all Steam UI, and
this guide does not authorize force-closing it.
