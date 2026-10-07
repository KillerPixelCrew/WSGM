# Steam CEF architecture and ownership

## Attended debugging startup prerequisite

Before any interactive debugger/CDP/MCP connection, including target discovery, require current
Steam-log evidence that Steam and Big Picture have fully started. Attaching early can hang the whole
Steam UI and leave Steam requiring force-close; do not perform that recovery without explicit
direction. See [live-tools.md](../../wsgm-steam-cef-debugging/references/live-tools.md). The
production mechanism below is not a substitute for that log check and currently does not parse Steam
logs.

## End-to-end path

```text
Steam / SteamMonitor
  -> SteamUiReadiness
  -> ShellSession transport gate
  -> PersistentSteamUiTransport
  -> SteamUiSessionHost
  -> SteamUiPatchManager + SteamUiBridgeHost + SteamUiModuleRuntime
  -> SteamUiToolkit surfaces and rows
  -> WSGM NativeQam adapters and managers
```

`SteamUiReadiness` is the lifecycle authority. `PersistentSteamUiTransport` owns CDP discovery,
target generations and one connection per target role. `SteamUiSessionHost` composes the patch
manager, bridge, and registered modules for that generation. Feature services publish state or route
commands through the host; they do not attach their own CDP clients.

## Layer ownership

| Owner                | Responsibilities                                                                                                                                                                                              | Primary locations                                                                           |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| WSGM shell           | Readiness, feature policy, module registration, state publication, command routing, WSGM backends                                                                                                             | `src/WSGM/Shell/SteamUi*`, `src/WSGM/Shell/NativeQam*`                                      |
| WSGM core            | WSGM-only policy on top of those calls: library tabs, card badge, download sort, which artwork slot, which launch wrapper, which card's library, glyph delivery                                               | `src/WSGM/Core/Steam*.cs`, `src/WSGM/Core/Library*.cs`, `src/WSGM/Core/SteamUiAssets`       |
| SteamUiToolkit       | CDP discovery/transport, generations, bridge, patch lifecycle, ownership primitives, Steam module contracts, reusable Valve-backed surfaces and rows, and the client layer that reads and drives Steam itself | `external/steam-ui-toolkit/src`, `external/steam-ui-toolkit/tests`                          |
| WindowsDeviceControl | Reusable Windows audio, radio, brightness, and related OS device primitives below WSGM policy                                                                                                                 | `external/windows-device-control/src`, `external/windows-device-control/tests`              |
| Generated boundary   | One composed runtime asset, hashed by the catalog when it loads                                                                                                                                               | `src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js`, `src/WSGM/Core/SteamUiAssetCatalog.cs` |

The toolkit is a pinned submodule dependency, not a source staging folder. If behavior is reusable
by another host, implement it in the toolkit and make WSGM a thin adapter. If behavior is
specifically WSGM policy or a WSGM service, keep it in WSGM.

## Target roles

### SharedJSContext

This headless target owns webpack modules, React, Steam stores, the bridge, and resident patches. It
has no useful visible DOM. A new context creates a new generation and invalidates stale operations.

### MainWindow

This is the visible Big Picture page used for DOM work and screenshots. Select it by URL shape:
`about:blank?` with `createflags` and `minwidth`, without `openerid` or `browserviewpopup`. Titles
are localized and therefore not an identity.

## Readiness and shutdown order

The gate is:

```text
master && !exitPending && ((!inGameMode && !transitionPending) || bigPictureReady)
```

In game mode, keep the transport closed until the real `SDL_app` Big Picture window exists. Before a
Big Picture request, retract the host, card badge, and library tabs, then close the transport within
the bounded shutdown budget. Disabling the master switch follows the same retract-before-close
order. Overlay-test mode never attaches.

The toolkit transport additionally requires one validated MainWindow target before attaching in
either mode (`requireMainWindow: true` in WSGM). The desktop master-switch branch permits discovery,
not headless startup injection. Remote-debugging opt-in receives the configured master switch
separately, so the startup hold cannot suppress the flag on a first cold start.

Module scans and resolution belong to `SteamUiModuleResolver` in the toolkit. WSGM features supply
their fingerprints and choose exports by shape; they use the same resolver source as probes and
gates. Module ids and export names are per client build and never appear in code: the September 2026
beta renumbered every module. `eng/check-steam-fingerprints.mjs` checks every fingerprint against
the installed client's bundle.

A healthy cold start orders evidence as:

1. `Big Picture window detected`
2. `Steam UI transport open: Big Picture window is up.`
3. each required patch reaches `Applied` and `Verified`

Patches appearing before the window signal indicate a readiness regression; this previously caused
headless startup hangs.

## Patch and ownership model

Each patch declares its stable id, target role, bounded phase budget and `probe/apply/verify/remove`
methods. Probe returns a semantic fingerprint. The manager has no dependency graph or per-resource
locks: every pass runs under one scheduler gate, applies the bridge first and removes it last, and
cancels stale work when the generation changes. An apply without verification is rolled back. A
verified patch with the same fingerprint is re-verified instead of blindly reapplied.

Use one of three narrow ownership mechanisms:

1. Supply a missing namespace without replacing a real one.
2. Claim a specific member or RPC and restore its exact original.
3. Reveal a narrowly scoped getter or value and restore it.

Store ownership metadata on a durable object or string marker, not a closure or `Symbol` that a new
evaluation cannot recover. Removal must be idempotent and must not disturb Valve or another tool.

## Bridge and module contract

The bridge exposes only the request and publication kinds declared by registered modules. It is not
a generic evaluation, shell, or device endpoint. Preserve strict camelCase envelopes, positive
sequence and action-generation values, generation checks and replay protection. Host state and
responses stream in 262,144 UTF-16-character parts without an aggregate bridge cap. Page requests
are not streamed: the CDP notification parameters have a 1 MiB cap.

First decide whether "Valve-backed" means exposing an existing Valve-owned surface or building a
WSGM row from known Valve controls. Those require different discovery, ownership, state-polarity,
and placement evidence. Extend an existing surface, gate, and publication when they already own the
backend; a new row alone is not a reason to duplicate them.

A genuinely new reusable surface normally contributes:

- a TypeScript gate plus install/verify/remove logic;
- a C# patch or surface type and, where applicable, row definition;
- typed state and command vocabulary;
- JSON context and validators;
- focused toolkit tests.

WSGM then supplies the adapter, backend service, state projection, command handling, feature policy,
module registration, and WSGM tests.

Quick Settings and Performance placement have distinct diagnostics. Generic patch verification can
succeed before a particular panel has rendered, so inspect the panel-specific root-resolution and
append outcome rather than treating one generic append field as proof for both.

## Generated runtime

`eng/build-steam-assets.mjs` composes toolkit TypeScript fragments with WSGM fragments in the order
the toolkit's `eng/steam-ui-fragments.mjs` defines, strips the supported TypeScript syntax, formats
the result and writes `NativeQamBootstrap.js`; the catalog hashes the embedded bytes at load. The
generated file is evidence of the current composition, not an editing surface.

The toolkit's `SteamUiExtensionHost` validates `extension.steam-ui.json` packages but runs no code;
WSGM does not use it as its package loader. WSGM's `CommonPluginSteamUiSource` instead composes
admitted `SteamPluginFrontendSurface` modules with the host modules. Those bundles deliberately run
unrestricted JavaScript/CSS and contribute native pages/menus/QAM/library slots. They have
owner-wide failure teardown, not a security sandbox. They are separate from host-rendered bounded
Extensions-tab descriptors. Host surfaces follow CEF master and remain enabled with custom QAM rows
off. Patch enablement, publication enablement and data availability are separate decisions.

## The client layer

`external/steam-ui-toolkit/src/SteamUiToolkit/Client` owns one-shot calls into the running client:
app details and launch writes, custom artwork, install folders, the download overview, library data,
the game page in view, and the running-app observer `SteamRunningAppsProbe` reads. These are not
patches; nothing is installed in the page except that one observer, which its lease removes.

A new call against `SteamClient.*` or a Steam store belongs there, not in WSGM. WSGM keeps the
policy above it: `Core\SteamLibraryFolders.cs` resolves a card's content id to one library path and
refuses an ambiguous one, `Core\SteamLaunchConfig.cs` owns the launch wrapper,
`Core\SteamArtwork.cs` owns slot rules and local art lookup, `Shell\KeepAwakeService.cs` owns what a
download sample means for the wake lock, and `Shell\RunningApplicationTarget.cs` owns RTSS pairing
and the projection.

## UI ownership and required Overlay parity

Every new or changed Big Picture capability or workflow also belongs in Overlay. Both presentations
must use the same service/projection and validated semantic command, with consistent capability
gates, pending/refusal state, cancellation and disposal. The view only chooses its native controls
and navigation. Do not move policy into a renderer or build an independent backend for the second
UI. A missing counterpart is incomplete work unless the maintainer explicitly scoped an exception.

Use [reusable-elements.md](reusable-elements.md) to select the toolkit surface, native field, UI kit
primitive or typed client call. Use [ui-and-overlay-parity.md](ui-and-overlay-parity.md) for the
Avalonia counterpart. Reusable Steam discovery/ownership/rendering goes in SteamUiToolkit; reusable
Windows operations go in WindowsDeviceControl; WSGM owns feature policy and two thin presentations.
