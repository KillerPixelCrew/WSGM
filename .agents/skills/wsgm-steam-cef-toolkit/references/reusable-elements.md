# Reusable Steam UI elements and implementation choices

Read the [startup preflight](../../wsgm-steam-cef-debugging/references/live-tools.md) before any
live CEF connection. Everything below can be selected and understood from source while Steam remains
disconnected. Start with the existing element and shared owner; add a new abstraction only when an
actual requirement cannot fit them cleanly.

Every new or changed Big Picture capability or workflow must also be available in WSGM's Overlay.
Both presentations use the same service, state and semantic operations. Match availability, pending
state, validation/refusal, cancellation and cleanup, while using each UI's native navigation and
controls. Do not duplicate a Windows/device backend or policy to achieve parity. The
[Overlay style and parity guide](ui-and-overlay-parity.md) names the Avalonia elements and review
steps; an absent counterpart is unfinished work, not an implicit exception.

## Choose the existing mechanism

| Need                                                | Use                                                                                | Keep in WSGM                                                               |
| --------------------------------------------------- | ---------------------------------------------------------------------------------- | -------------------------------------------------------------------------- |
| Read or change Steam's own data once                | One shared `SteamClient`, its typed client operations below                        | Which game/library/artwork slot, user choice and recovery policy.          |
| Keep a Valve surface alive with Windows state       | The matching typed `Steam*Surface.Module` or `Steam*Row.Module`                    | Service state, capability projection, validation and actual writes.        |
| Add fields to native Settings                       | `SteamNativeSettingsSurface` + `SteamSettingsRow` descriptors                      | Native page selection and the same capability on Overlay.                  |
| Add dynamic settings to QAM Performance             | `SteamSettingsQuickAccessRow` with the same descriptors                            | Current keys, revision and backend.                                        |
| Build a product page                                | `SteamPageSurface` routes + `registerSteamPage` renderer + `SteamPagePatch.Create` | Product data/commands; use native fields and the toolkit UI kit.           |
| Add an ordinary plugin action or setting            | `SteamExtensionsTabSurface` or `SteamGameContextMenuSurface`                       | Admission and bounded descriptors; secrets remain write-only.              |
| Host a plugin's custom JavaScript/CSS               | `SteamPluginFrontendSurface` on the existing runtime                               | Explicit trust/admission, owner identity, reload and backend drain policy. |
| Share a React or property hook                      | Toolkit ownership/shared interceptors                                              | Feature fingerprint and transform; never a second wrapper.                 |
| Add a reusable visual primitive absent from the kit | Extend `ui-kit.ts` or the native field helper, with an emitted-asset contract      | Product labels and state; no one-off imitation in a consumer fragment.     |

The [complete source map](../../../../external/steam-ui-toolkit/docs/code-map.md) names every C# and
TypeScript implementation file. The
[reference](../../../../external/steam-ui-toolkit/docs/reference.md) contains limits and exact
contracts; member/parameter XML beside each public declaration is the API authority. Use these
catalogs rather than writing a second registry or speculative compatibility wrapper.

## Transport, bridge and module building blocks

- `SteamCef`: debugging opt-in, endpoint/owner predicates and JavaScript string encoding.
  `PersistentSteamUiTransport` implements `ISteamUiTransport`; leases share one connection per
  target role. `SteamUiTargetRole`, `SteamUiTransportHealth`, `SteamUiGenerations`,
  `SteamUiTransportSnapshot`, `SteamUiEvaluationResult`, `SteamUiDispatch` and `SteamUiNotification`
  keep role, readiness, six generation counters and execution uncertainty explicit. Production
  readiness remains the host's decision.
- `ISteamUiPatch`, `SteamUiPatchContext`, probe/operation results, state and snapshots describe one
  owned change. `SteamUiPatchManager` serializes phases and rollback. `SteamUiPatchEvaluation` reads
  structured outcomes. `SteamUiBridgePatch`, `SteamGatePatch`, `SteamQuickAccessRowPatch` and
  `SteamPagePatch` implement existing lifecycle patterns; prefer these over a custom scheduler.
- `SteamUiInjectedAsset` and `SteamUiBridgeIdentity` supply script/hash and stable names.
  `SteamUiBridgeHost` installs the binding, authorizes requests and delivers whole states/responses.
  Do not add a second channel to move larger state; existing host delivery is streamed.
- `ISteamUiModule`, `SteamUiModule`, `SteamUiModuleSet`, `SteamUiModuleBuilder`,
  `SteamUiStatePublication`, `SteamUiCommandHandler`, `SteamUiCommandDelegate`,
  `SteamUiPayloadReader<T>`, `SteamUiCommandResult` and `SteamUiModuleFailure` group patches, typed
  publications and exact commands. `SteamUiModuleRuntime` handles publication coalescing,
  replacement, cancellation and quarantine. Null readers publish nothing; explicit empty state
  clears content. Revision callbacks change when the published content changes.
- `SteamUiModuleResolver` and `SteamUiProbeJs` centralize source fingerprints/export shapes;
  `SteamUiPayload` supplies bounded payload readers and `SteamSurfaceJsonContext` supplies the
  built-in camelCase serializers. Consumer state uses its own source-generated JSON metadata.
- `SteamUiExtensionHost`, `SteamUiExtension`, `SteamUiExtensionManifest` and
  `SteamUiExtensionRejection` validate file packages but execute nothing. This is separate from WSGM
  package hosting and from unrestricted frontend runtime loading.
- `ISteamUiLog` and `SteamUiLog` report stable diagnostics. Preserve complete returned operation
  detail while bounding log lines and redacting command string values.

Their
[source and lifetime map](../../../../external/steam-ui-toolkit/docs/code-map.md#transport-and-lifecycle)
and
[module contract](../../../../external/steam-ui-toolkit/docs/reference.md#7-modules-and-the-runtime)
are the starting points for host integration.

## Every built-in surface family

Factories own their state/backend contracts and exact command vocabulary. Use the
[surface file map](../../../../external/steam-ui-toolkit/docs/code-map.md#surface-implementations)
for each file's declarations and the
[surface reference](../../../../external/steam-ui-toolkit/docs/reference.md#15-surfaces) for wire
and frontend behavior.

| Family                             | Reusable contracts                                                                                                                                                                                                                                                                                               |
| ---------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Audio/radio/display                | `SteamAudioSurface`, `SteamNetworkSurface`, `SteamBluetoothSurface`, `SteamBrightnessSurface`, `SteamResolutionRow`, `SteamAudioFormatRow`.                                                                                                                                                                      |
| Performance and power              | `SteamPerformanceSurface`, `SteamPowerLimitSurface`, `SteamAutoTdpRow`, `SteamFrameLimitRow`, `SteamVariableRefreshRow`, `SteamPowerProfileRow`, `SteamPowerPresetRow`, `SteamCpuBoostRow`, `SteamHybridCoreRow`; `SteamPerformanceDeltaReader` and `SteamOverlayLevelWire` interpret native performance values. |
| Device controls                    | `SteamControllerTargetRow`, `SteamDeviceControlsRow`; publish the host's current capabilities rather than probing hardware from a view.                                                                                                                                                                          |
| QAM composition                    | `SteamQuickAccessLayoutSurface`, `SteamPanelFoldsSurface`, `SteamSettingsQuickAccessRow`; final composition keeps Reset to Default last, including dynamic/plugin rows.                                                                                                                                          |
| Native Settings                    | `SteamNativeSettingsSurface`, `SteamNativeSettingsPageId`, `SteamNativeSettingsPage`, `SteamNativeSettingsState`; Display, Power, Audio and Controller slots retain Valve content.                                                                                                                               |
| Settings descriptors               | `SteamSettingsRowKind`, `SteamSettingsRow`, `SteamSettingsChoice`, `SteamSettingsConfirmation`, `SteamSettingsSection`, `SteamSettingsPage`; reuse descriptors in host settings and QAM where supported.                                                                                                         |
| Navigation and pages               | `SteamNavigationPanelSurface`, `SteamPageSurface`, `SteamFilePickerSurface`; native anchors/routes/back-stack/modal behavior.                                                                                                                                                                                    |
| Library/storage                    | `SteamLibraryBadgeSurface` (badge and details), `SteamHomeCarouselSurface`, `SteamStorageSurface`; host supplies library identities and storage actions.                                                                                                                                                         |
| Native timeout/menu additions      | `SteamScreensaverSurface`, `SteamPowerMenuSurface`; report native timeout state or dispatch the host's desktop transition.                                                                                                                                                                                       |
| Host-rendered plugin UI            | `SteamExtensionsTabSurface`, `SteamGameContextMenuSurface`; settings/actions on host-owned native surfaces.                                                                                                                                                                                                      |
| Theme/audio assets                 | `SteamThemeStyleSurface`, `SteamSoundOverrideSurface`; stylesheet ownership and revision-bound sound decoding. Installation/licensing/selection are host services.                                                                                                                                               |
| Arbitrary plugin UI                | `SteamPluginFrontendSurface`; an unrestricted frontend bundle with owner-wide failure containment, not a sandbox.                                                                                                                                                                                                |
| Native window observations/actions | `SteamOverlayActivationPatch`, `SteamSideMenuSnapshot.ReadAsync`, `SteamNativeSurfaceCommands.ReplayAsync`, `SteamGameWindowActivation.RaiseAsync`, `SteamRouteNavigation.NavigateAsync`; exact observed identity/generation, no uncertain retry or fallback.                                                    |

## Typed Steam operations

Compose one `SteamClient` over the session transport. Existing clients and
[client contracts](../../../../external/steam-ui-toolkit/docs/reference.md#16-the-client-layer)
cover:

| Client property  | Operations                                                                                                                                                                                                                                                                                                                                                                                                         |
| ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Apps`           | `ReadDetailsAsync`, `ReadAccountIdAsync`; `SetLaunchOptionsAsync`, `SetShortcutLaunchAsync`; `ListShortcutsAsync`, `AddShortcutAsync`, `RemoveShortcutAsync`, `IsShortcutAppId`; `SetCustomArtworkAsync`, `ClearCustomArtworkAsync`; `SetShortcutIconAsync`, `ClearShortcutIconAsync`, `RefreshIconAsync`, `ReadOfficialIconUrlAsync`; `SaveLogoPositionAsync`, `ClearLogoPositionAsync`, `ReadLogoPositionAsync`. |
| `InstallFolders` | `NormalizePath`, `AddAsync`, `RemoveAllAtPathAsync`, `SetLabelAsync`. Folder ids are not array indices.                                                                                                                                                                                                                                                                                                            |
| `Collections`    | `SyncAsync` for host-owned user collections.                                                                                                                                                                                                                                                                                                                                                                       |
| `Library`        | `ReadGamesAsync`, `ReadCollectionsAsync`, `ReadStoreTagsAsync`; failed reads are not an empty library.                                                                                                                                                                                                                                                                                                             |
| `Downloads`      | `QueryAsync` and `IsActive`; wake-lock policy belongs to WSGM.                                                                                                                                                                                                                                                                                                                                                     |
| `CurrentPage`    | `GetAsync`; a visible game page does not prove that game is running.                                                                                                                                                                                                                                                                                                                                               |
| `RunningApps`    | `SubscribeAsync`, `ObserveAsync`, `ReadDetailsAsync`; single-reader observer removed by its lease.                                                                                                                                                                                                                                                                                                                 |
| `StartupMovie`   | `SetAsideAsync`, `RestoreAsync`; the host saves the displaced choice and owns override-file policy.                                                                                                                                                                                                                                                                                                                |

`SteamClient.EvaluateAsync` is available for a repository-owned expression when no typed operation
fits. A new reusable Steam call belongs in the toolkit client layer, not in a WSGM-local transport.
`SteamReadResult<T>`, `SteamClientWriteOutcome` and each operation's result distinguish refusal, no
dispatch and a potentially executed write. Never retry `Unknown` automatically.

## Native fields and visual style

Resolve Steam's components through `resolveSteamUiComponents` or `resolveSteamSettingsComponents`;
use Steam's React, Focusable, ToggleField, DropDownField, SliderField, TextField, DialogButton, tabs
and modals. Use `SteamUiTabbedPageRequired` or `SteamSettingsRequired` to make missing components an
explicit incompatibility. Do not draw an HTML imitation when the native field exists.

`SteamSettingsRowKind` covers `boolean`, `choice`, `range`, `text`, `secret`, `order`, `action`,
`note` and `color`. Native Settings intentionally accepts the subset boolean/choice/range/text/
color/action/note. Secret values are never published; a placeholder says whether one is set. Sliders
send on completion, text sends on changed blur, and hardware RGB uses `ColorAlpha = false`. The
color editor stages changes and sends on Save; cancelling sends nothing.

Everything around native fields comes from `ui-kit.ts`, rendered with the same resolved `ui`:

| Need                        | Helpers                                                                                                     |
| --------------------------- | ----------------------------------------------------------------------------------------------------------- |
| Styles and sections         | `steamUiKitStyle`, `renderSteamUiHeader`, `renderSteamUiGroup`.                                             |
| Actions and pagination      | `renderSteamUiActions`, `renderSteamUiMore`.                                                                |
| Page/focus levels           | `renderSteamUiPane`, `renderSteamUiLevel`, `renderSteamUiTabbedPage`, `renderSteamUiDetail`.                |
| Color and empty/error state | `renderSteamUiSwatch`, `renderSteamUiEmpty`, `renderSteamUiBanner`.                                         |
| Cards and media             | `renderSteamUiCard`, `renderSteamUiGrid`, `renderSteamUiGallery`, `renderSteamUiVideo`.                     |
| Inline controls             | `renderSteamUiToolbar`, `renderSteamUiTool`, `renderSteamUiChips`, `renderSteamUiBox`.                      |
| Glyphs                      | `renderSteamUiGlyph`, `SteamUiGlyphs`, `renderSteamGlyph` and the shared `icons.ts` vocabulary.             |
| Dialogs                     | `showSteamUiConfirm`, `showSteamUiPrompt`, `showSteamModal`, `showSteamFilePicker`, `showSteamColorEditor`. |

Render `steamUiKitStyle(react)` once per consuming document root. Keep flat `steam-ui-kit-*`
classes, Steam panel spacing/vocabulary, Focusable controller behavior and the `gpfocus` highlight.
Use the native row's icon slot; slider icons belong at the front. Do not repeat the same glyph for a
section and its row. Preserve native content/labels, stable descriptor anchors and back-stack/B
behavior. Extend one shared element for a missing need instead of copying CSS or markup into a
consumer page. See the complete
[UI kit contracts](../../../../external/steam-ui-toolkit/docs/reference.md#the-ui-kit).

## Shared script API and cleanup

The complete
[consumer script API](../../../../external/steam-ui-toolkit/docs/reference.md#script-api-for-consumer-fragments)
lists the supported names. They share one IIFE scope; they are not extra window globals:

- Pages/settings: `registerSteamPage`, `renderSteamSettings`, `renderSteamSettingRow`,
  `useSteamSettingDrafts`, the resolvers and required-component sets above. Use drafts for pending
  values/refusals; do not build a second cache or hide a refused write as success.
- Native presentation: `createSteamCapsule`, `resolveSteamLibraryClasses`, `renderSteamDropdown`,
  `steamCheckbox`, `SteamGamepadButton`, `onSteamTriggers`, `navigateSteamRoute`.
- Bridge/gates: `request`, `subscribe`, `registerGate`. Use the existing gate/page subscription
  lifetime; the bridge supplies cached replay and current generation identity.
- Discovery/query/lifetime: `getWebpackRuntime`, `JsxRuntimeTokens`, `invalidateQuery`,
  `endSubscription`, `keyed`. The resolver's source-count operations do not load a factory;
  resolving exports may, so startup readiness remains mandatory.
- Shared ownership: `claimMember`, `memberClaimed`, `releaseMember`; `interceptMemo`,
  `memoIntercepted`, `releaseMemo`; `interceptElements`, `elementsIntercepted`, `releaseElements`.
  Toolkit gates also share namespace/value/accessor claims and exact descriptor restoration.
  Consumers must not own parallel wrappers or reach into draft caches, gate maps or claim records.

A plugin bundle's separate unrestricted `api` supplies `react`, `bridge`, `resolveModules`,
`resolveComponents`, `uiKit`; page/menu/QAM-tab/QAM-row/library/game-page/patch registrations;
`addStyle`, `call`, `subscribe`, `guard`, `ready`, `onDispose`, and guarded timer/listener helpers.
Use its [frontend contract](../../../../external/steam-ui-toolkit/docs/plugin-frontends.md) for
return values, budgets and whole-owner cleanup. An endless loop or crashed renderer is outside an
in-page error boundary's reach.

## Keep the change maintainable

One feature should read as a shared service/projection, a typed module or existing surface, and two
thin presentations. Keep policy out of controls and toolkit primitives, native ABI work in
WindowsDeviceControl, reusable Steam mechanics in SteamUiToolkit and product decisions in WSGM.
Prefer named small functions and explicit state transitions over a parallel registry, generic
framework, wrapper layer or copied helper. Reuse the existing cleanup/admission paths. Document new
shared elements where their existing catalog lives and update the paired Overlay feature before
calling the Big Picture work complete.
