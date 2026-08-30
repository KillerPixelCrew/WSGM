# WSGM 2.0 QAM overhaul — reactivated Steam performance UI and per-application profiles

Status: implementation plan, written 2026-08-30

Evidence: live-probed against the local Windows Steam client (SharedJSContext, CEF port 8080) and
measured on the reference MSI Claw 8 AI+ A2VM (`MS-1T52`, Arc 140V, driver `32.0.101.8991`).
Probe scripts are retained in `tools/WsgmLibTest/probe-perf-*.js`.

## Goal

Per-application performance profiles in both the native QAM and the WSGM overlay, matching stock
SteamOS behaviour, built by **reactivating Steam's own performance components and supplying the
backend they were written against** rather than hand-constructing rows.

WSGM currently builds every QAM row by hand from Valve's field primitives. That produced correct
controls with no per-app profile concept, no localized explainers, and a presentation that drifts
from stock Steam. The components for the real thing already ship in the Windows client.

## The finding this plan rests on

The SteamOS Performance tab is present and complete in the Windows Steam bundle. It is not hidden
behind an OS check — **its backend is simply absent**:

```js
constructor(){ makeAutoObservable(this);
  SteamClient.System.Perf?.RegisterForDiagnosticInfoChanges(this.OnDiagnosticInfoChanged),
  SteamClient.System.Perf?.RegisterForStateChanges(this.OnStateChanged) }
```

`SteamClient.System` has no `Perf` namespace on Windows, so the optional chaining no-ops, the store's
state stays `{}`, and every control renders `null`. Writes have the identical shape — each setter
builds a protobuf delta and hands it to `SteamClient.System.Perf?.UpdateSettings(...)`.

The whole integration is therefore one named seam. WSGM becomes the performance backend on Windows.

**This is not a SteamOS/Deck spoof and must not become one.** D16 forbids that, correctly. WSGM
claims no Deck identity, touches no unrelated gate, and must **never** set `force_deck_perf_tab` —
Valve's own gate override (`U(e) = e || force_deck_perf_tab`) is a *persisted client setting* that
would force-show every row including the ones WSGM cannot back. D16 should be amended to state that
supplying an absent `SteamClient.System.Perf` for a device WSGM can genuinely service is in scope,
so this is not re-litigated later.

## Hiding is free, and is the load-bearing safety property

Control availability is read straight out of the state WSGM supplies:

```js
function E(){ return [ q3(()=>msgLimits?.is_vrr_supported ?? false),
                       q3(()=>msgSettingsPerApp?.is_vrr_enabled ?? false), SetVRREnabled ] }
```

Omit a `limits` field and Valve's own wrapper renders `null`. Anything WSGM cannot back is therefore
hidden by construction, with no CSS and no patching.

Two layers, because one is not enough: some hooks hardcode `available: true` (the scaling filter and
scaler both return `[!0, …]`) and can never be hidden by state. WSGM mounts a **chosen subset** of
the exported components, so the first and primary layer is simply not rendering a control at all;
`limits` gating is the second layer for controls that are mounted but unsupported on the device.

## Ownership

| Owner | Responsibility |
| --- | --- |
| Device Plugin | Every hardware transport, including IGCL/Arc Sync. WSGM never chases a GPU driver |
| WSGM Core | The per-app profile store, frame-limit strategy policy, refresh pairing, RTSS binding |
| WSGM Steam adapter | The `SteamClient.System.Perf` shim, state projection, component mounting |
| WSGM overlay | The same services behind WSGM's own surface (D17) |

The plugin boundary is the maintainer's standing rule: **if it touches the device it belongs to the
Device Plugin**, so the burden of following driver changes sits with device plugin authors and not
with WSGM.

## Component inventory

Reused from Steam (module `83571`, selected by structural fingerprint, never by module id):

| Export | Control | Backed by |
| --- | --- | --- |
| `jw` | `PERFORMANCE SETTINGS` header, `Using <GAME> profile` + app icon | `active_profile_game_id` |
| `mR` | Use per-game profile toggle | `is_game_perf_profile_enabled` |
| `N1` | Basic-view profile line | `active_profile_game_id` |
| `PZ` | Basic / Advanced view | `global.is_advanced_settings_enabled` |
| `DJ` | Reset to default | `ResetCurrentPerfProfileSettings` |
| `Mq` | Frame limit notch slider | `limits.fps_limit_options`, `per_app.fps_limit` |
| `gv` | Performance overlay level | `global.perf_overlay_level` → RTSS |
| `by` | Refresh rate | `limits.display_refresh_manual_hz_min/max` |
| `bh` | VRR | `limits.is_vrr_supported`, `per_app.is_vrr_enabled` |

Reused over the SteamOS Manager RPC seam described below: **TDP limit**, and the charge limit the
same service carries.

Hand-built, on Valve's `SliderField`/`ToggleField`, because Steam has no component for them: AutoTDP,
controller target, and every other plugin-published capability.

Deliberately hidden: manual GPU clock, scaling mode/filter/sharpness, half-rate shading, allow
tearing, force composite, and Steam's own FPS overlay (RTSS owns that surface).

## Frame limit strategies — three configurable roads

The frame-limit slider's notches come entirely from `limits.fps_limit_options`, an integer array
WSGM supplies, and the setter is WSGM's to interpret. The strategy is a user setting, because the
right answer differs per device and per user tolerance for mode changes.

| Strategy | Behaviour |
| --- | --- |
| `FrameLimitOnly` | Cap frames via RTSS. Never touch the refresh rate. Safest; the default |
| `NativeModes` | Cap frames, and switch refresh only among EDID-native modes (60/120 on the Claw) |
| `FrameDoubling` | Cap frames, and pick the lowest runtime-validated mode that is an exact multiple |

`FrameDoubling` uses driver-synthesized modes, giving an exact-cadence pairing for many more caps:

| Cap | Refresh | Multiple |
| --- | --- | --- |
| 24 | 48 Hz | 2× |
| 25 | 75 Hz | 3× |
| 30 | 30 / 60 / 120 Hz | 1–4× |
| 40 | 120 Hz | 3× |
| 50 | 100 Hz | 2× |
| 60 | 60 / 120 Hz | 1–2× |

Rules that make this safe:

- **Discover, never hardcode.** Enumerate modes with `EnumDisplaySettings`, validate each candidate
  with `ChangeDisplaySettingsEx(CDS_TEST)`, and keep only what the driver accepts. The Claw offers
  30/48/60/75/100/120 while its EDID lists only 60 and 120; a panel without VRR will likely accept
  only its EDID modes, which is the Legion Go case that motivated this.
- **Prefer the lowest valid multiple**, because refresh rate is a power cost — a 30 FPS cap at 30 Hz
  costs meaningfully less than the same cap at 120 Hz.
- **Apply dynamically**, never with `CDS_UPDATEREGISTRY`. A dynamic change leaves the user's
  persisted display configuration untouched, so exit, crash, and reboot all self-heal.
- **Mode changes are not free.** Fullscreen-exclusive titles can hitch, minimize, or drop out on a
  mode set. With VRR present, `FrameLimitOnly` at a fixed 120 Hz is usually better, which is why it
  is the default and why frame doubling earns its place mainly below the VRR floor, for power
  saving, and on panels with no VRR at all.
- The resolved refresh is shown in the refresh-rate row. The fused `60 FPS (60 Hz)` label from stock
  SteamOS is **not reproducible** — this build's notch labels are `value.toString()` off an integer
  array. Reproducing it would mean hand-building the row again, which is what this work removes.

## Variable refresh rate via IGCL

Device-verified on the reference unit, unelevated, on 2026-08-30. `ControlLib.dll` ships with the
Intel driver and is already in `System32`; IGCL initialised at v1.1; the internal panel reported
`IsIntelArcSyncSupported = 1` across 30–120 Hz with the profile at `EXCELLENT`. A write to `OFF` and
a restore of the saved parameter struct both returned success, with the read-back confirming each.

| Capability surface | IGCL call |
| --- | --- |
| available | `ctlGetIntelArcSyncInfoForMonitor` → `IsIntelArcSyncSupported` |
| read | `ctlGetIntelArcSyncProfile` → `profile != OFF` |
| write | `ctlSetIntelArcSyncProfile` → `RECOMMENDED` / `OFF` |
| restore | save the params struct at cycle start, write it back verbatim on make-safe |

Implementation notes that cost real time to find: both enumerations are **two-call** (count with a
null buffer, then fetch — passing a buffer directly returns nothing); outputs must be selected by
which one answers, since unattached connectors return `CTL_RESULT_ERROR_KMD_CALL`; the external
display when docked is a different output, so the row must follow the active panel; and IGCL's
`bool` is one byte, so it is `byte` in C#, never `bool`.

IGCL is flat C with blittable structs, so it needs **no** Rust helper and no COM — unlike the radio
and volume helpers. `LibraryImport` with a resolver that degrades to unavailable when the DLL is
absent is sufficient, which keeps AMD and non-Intel devices reporting the capability as unsupported.

Because the read-back independently confirms the write (the range collapses to `120/120` when the
profile is `OFF`), this capability reports `readbackQuality: verified`, unlike most EC writes.

## The TDP row is reused, over the SteamOS Manager RPC

A TDP Limit control does exist in stock SteamOS. In this client it binds to
`steamos_tdp_limit_enabled`/`steamos_tdp_limit` (client settings, fields 22001/22002), with
availability from `SteamOSService/State/Manager` fetched over a WebUI transport RPC:
`GetState()` → `is_tdp_limit_available`, `tdp_limit_min`, `tdp_limit_max`. The perf-store family has
no TDP component at all — zero `tdp` occurrences in `83571`.

So this is a **second seam of a different shape**: an RPC service response rather than an absent JS
namespace. WSGM supplies that response, exactly as it supplies `SteamClient.System.Perf`, and the
row is reused rather than hand-built. The same seam carries `is_charge_limit_available` with
`charge_limit_min/max/default`, which is the Claw's outstanding charge-limit work, and
`is_manual_gpu_clock_available`.

That seam is deeper than the Perf namespace and must be treated accordingly: its own patch id,
fingerprint, verification, removal, and kill switch, so a client that changes the service shape
loses the TDP row and nothing else.

## Rejected, with evidence, so it is not re-litigated

- **`force_deck_perf_tab`.** A persisted client setting that force-shows controls WSGM cannot back.
- **The gamescope frame-limit family.** `gamescope_enable_app_target_framerate`,
  `gamescope_app_target_framerate`, `gamescope_disable_framelimit` gate a
  `#QuickAccess_Tab_Perf_AppRefreshRate` row behind a gamescope feature check. Not reachable.
- **Setting `TS.IS_STEAMOS`.** The global platform constant behind several Deck-only gates. Changing
  it is the D16-forbidden spoof and would alter unrelated client behaviour. Where a Deck-only gate
  blocks a subsystem WSGM can genuinely back, the narrow override of that one gate is the supported
  move — never the global constant.

## Implementation slices

1. **SDK** — a VRR capability role and display key, so the plugin publishes it and WSGM only
   projects it. No device-specific detail in the SDK.
2. **Claw plugin** — the IGCL transport: dynamic load, capability detection, read, write, restore on
   make-safe, and `PluginTrace` on every decision including each refusal and its observed values.
3. **Core** — the per-application profile store keyed by app id, the frame-limit strategy setting,
   runtime mode discovery with `CDS_TEST` validation, and the pairing policy. Pure decision helpers
   kept `internal` and unit-tested; no device or Steam types.
4. **Steam adapter** — the `SteamClient.System.Perf` shim: build `CMsgSystemPerfState` through the
   client's own message classes and deliver it via the store's bound `OnStateChanged`; accept
   `UpdateSettings` deltas and route them to the same services the overlay uses. One patch per
   mounted component, each with its own fingerprint, verification, removal, and kill switch, so one
   incompatible control never takes the panel down.
5. **Overlay** — the same services behind WSGM's own surface, since the overlay is the complete one.
6. **Retire** the hand-built rows that the reactivated components replace, keeping TDP and the
   plugin-published capabilities.

## Validation

Automated: pairing and strategy selection as pure decision tests; mode-discovery filtering; state
projection and delta parsing; profile store round trips through the existing isolated-HKCU pattern.

Attended on the reference device, and not automatable: the live Steam matrix with the panel mounted,
per-game profile switching against a real game, each frame-limit strategy including an exclusive
fullscreen title across a mode change, VRR toggling with a rendering game, and recovery when Steam
restarts underneath the shim.
