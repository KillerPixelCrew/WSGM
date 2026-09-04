# Steam CEF architecture and ownership

## End-to-end path

```text
Steam / SteamMonitor
  -> SteamUiReadiness
  -> ShellSession transport gate
  -> PersistentSteamUiTransport
  -> SteamUiTransportSession
  -> SteamUiSessionHost
  -> SteamUiPatchManager + SteamUiBridgeHost + SteamUiModuleRuntime
  -> SteamUiToolkit surfaces and rows
  -> WSGM NativeQam adapters and managers
```

`SteamUiReadiness` is the lifecycle authority. `PersistentSteamUiTransport` owns CDP discovery,
target generations, and the single session. `SteamUiSessionHost` composes the patch manager, bridge,
and registered modules for that generation. Feature services publish state or route commands through
the host; they do not attach their own CDP clients.

## Layer ownership

| Owner                | Responsibilities                                                                                                                                     | Primary locations                                                                           |
| -------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| WSGM shell           | Readiness, feature policy, module registration, state publication, command routing, WSGM backends                                                    | `src/WSGM/Shell/SteamUi*`, `src/WSGM/Shell/NativeQam*`                                      |
| WSGM core            | WSGM-only library tabs, card badge, downloads and sort, artwork, libraries, launch options, glyph delivery                                           | `src/WSGM/Core/Steam*.cs`, `src/WSGM/Core/Library*.cs`, `src/WSGM/Core/SteamUiAssets`       |
| SteamUiToolkit       | CDP discovery/transport, generations, bridge, patch lifecycle, ownership primitives, Steam module contracts, reusable Valve-backed surfaces and rows | `external/steam-ui-toolkit/src`, `external/steam-ui-toolkit/tests`                          |
| WindowsDeviceControl | Reusable Windows audio, radio, brightness, and related OS device primitives below WSGM policy                                                        | `external/windows-device-control/src`, `external/windows-device-control/tests`              |
| Generated boundary   | One composed runtime asset and its SHA-256 catalog entry                                                                                             | `src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js`, `src/WSGM/Core/SteamUiAssetCatalog.cs` |

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
master && ((!inGameMode && !transitionPending) || bigPictureReady)
```

In game mode, keep the transport closed until the real `SDL_app` Big Picture window exists. Before a
Big Picture request, retract the host, card badge, and library tabs, then close the transport within
the bounded shutdown budget. Disabling the master switch follows the same retract-before-close
order. Overlay-test mode never attaches.

A healthy cold start orders evidence as:

1. `Big Picture window detected`
2. `Steam UI transport open: Big Picture window is up.`
3. each required patch reaches `Applied` and `Verified`

Patches appearing before the window signal indicate a readiness regression; this previously caused
headless startup hangs.

## Patch and ownership model

Each patch declares its resource, dependencies, fingerprint, kill switch, and bounded
`probe/apply/verify/remove` phases. The manager serializes patches that share a resource and cancels
stale work when the generation changes. An apply without verification is rolled back. A verified
patch with the same fingerprint is re-verified instead of blindly reapplied.

Use one of three narrow ownership mechanisms:

1. Supply a missing namespace without replacing a real one.
2. Claim a specific member or RPC and restore its exact original.
3. Reveal a narrowly scoped getter or value and restore it.

Store ownership metadata on a durable object or string marker, not a closure or `Symbol` that a new
evaluation cannot recover. Removal must be idempotent and must not disturb Valve or another tool.

## Bridge and module contract

The bridge exposes only the request and publication kinds declared by registered modules. It is not
a generic evaluation, shell, or device endpoint. Preserve the strict camelCase envelope, size cap,
positive sequence and action-generation values, generation checks, and replay protection.

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

`eng/build-steam-assets.mjs` composes toolkit TypeScript fragments with WSGM fragments, strips the
supported TypeScript syntax, formats the result, writes `NativeQamBootstrap.js`, and updates the
catalog SHA-256. The generated file is evidence of the current composition, not an editing surface.

The browser-extension host under the toolkit is a separate host and test surface. Its presence does
not mean WSGM mounts that extension or shares its lifecycle.
