# WSGM 2.0 Quick Settings, Internet, and Bluetooth revival

Status: implementation plan, written 2026-08-30

Evidence: live-probed against the local Windows Steam client with the QAM Quick Settings and the
Internet settings page open. Probes retained as `tools\WsgmLibTest\probe-qam-*.js` and
`probe-network-*.js`.

## Goal

Revive Steam's own Quick Settings tab and its Internet and Bluetooth settings pages, driving them
with WSGM's data. Same principle as `_plan\qam-overhaul.md`: Valve's components, WSGM's backend.
Everything in these surfaces is drivable — where Steam has no Windows backend, WSGM already owns the
mechanism.

## Read this before planning any of it

WSGM already owns the backend and has already revived part of this surface. None of the work below
is "implement Wi-Fi and Bluetooth"; it is adapters over finished code, and the files below record
behaviour that was established on hardware.

| Existing | What it already does |
| --- | --- |
| `Shell\RadioManager.cs` | Wi-Fi and Bluetooth state and control: `SetRadioAsync`, `ConnectAsync(ssid, password)`, `DisconnectAsync`, `ForgetAsync`, `SetAudioConnectionAsync`, `UnpairAsync`, `RespondToPairing`, scanning, pairing prompts with PIN |
| `Shell\RadioEntries.cs` | The network and device row models, including `WifiSecurity` |
| `Interop\NativeRadio.cs` | The blittable bridge to the radio helper |
| `Core\SteamNetworkIndicator.cs` | Already injects a synthetic access point into Steam's store for the header icon, and records what the Windows backend does and does not send |
| `Shell\NetworkIndicatorService.cs` | The push loop: change detection, retry while Steam is down, periodic heal because a Steam restart wipes the resident script |

The Steam operations map almost one to one onto `RadioManager`, so each replaced service method is a
thin adapter, not an implementation. `NetworkIndicatorService` is the residency and heal pattern to
extend rather than reinvent.

## Four kinds of gate, and what is allowed against each

This is the load-bearing distinction for all Steam UI revival work, not just this page.

| Gate | Example | Response |
| --- | --- | --- |
| Absent JS namespace | `SteamClient.System.Perf` | Supply it |
| Absent RPC response | `SteamOSService/State/Manager` | Supply it |
| RPC service stub with no backend | `BluetoothManagerService` (`RF`) | Replace the stub's methods |
| Deck-only store getter | `networkManagementAvailable` | Override that one getter |
| Global platform constant | `TS.IS_STEAMOS` | **Never.** D16 forbids it |

`networkManagementAvailable` is literally `get networkManagementAvailable(){return TS.IS_STEAMOS}`.
Overriding that one property is narrow and reversible and affects one surface. Setting the constant
it reads would change unrelated client behaviour everywhere and is the forbidden spoof. Every gate
below is handled at the narrowest level that works.

## What the tab contains

Module `79476`, function `De`. Sections in render order: Brightness, Audio, Other (airplane, Wi-Fi,
Bluetooth, night mode, reorder controllers), Controller, Game recording, Display scaling.

| Row | Gate | Windows backend | Plan |
| --- | --- | --- | --- |
| Brightness | `is_display_brightness_available ?? true` | `System.Display.SetBrightness` present | Reuse |
| HDR badge / heatmap | HDR output active | display store | Reuse where HDR is on |
| Display scaling, underscan | display store | `System.Display.SetUnderscanLevel` present | Reuse |
| Controller list, battery, Identify | controllers present | `SteamClient.Input` | Native already |
| Reorder controllers | controllers present | Steam Input | Native already |
| Game recording | client setting | native | Native already |
| Audio output, input, volume | `null != SteamClient.System.Audio` | namespace absent | Supply it; own plan |
| Wi-Fi toggle and network list | `networkManagementAvailable` | device state live, no access points | Override the getter, supply the list |
| Bluetooth toggle and devices | `BluetoothManagerService.GetState` | service round-trips, no backend | Replace the stub methods |
| Airplane mode | Wi-Fi or Bluetooth available | follows both | Falls out once both are on |
| Night mode | `IN_GAMESCOPE` | none | Back with Windows Night Light |
| Resolution | **not in this tab at all** | — | Add as a WSGM row |
| Refresh rate | in the Performance tab, not here | — | Reuse that component here |

## Wi-Fi: the device is real, the network list is entirely ours

Read `Core\SteamNetworkIndicator.cs` before touching any of this. It already solves the header
indicator and records what the Windows backend does and does not do, device-verified.

What Steam's Windows backend genuinely provides: periodic `CMsgNetworkDevicesData` reports carrying
real adapters, MACs and IPs, with the wireless device reporting `estate` Connected. The store's
`hasWirelessDevice` and `isWifiEnabled` are therefore true on Windows without any help.
`SteamClient.System.Network` exposes `SetWifiEnabled`, `StartScanningForNetworks`,
`StopScanningForNetworks`, `RegisterForDeviceChanges`, `ForceRefresh`, and a `Device` API with
`Connect`, `Disconnect`, `SetOptions`, `WirelessNetwork`. The store already implements
`accessPoints`, `userVisibleAccessPoints`, `Connect`, `ForgetAllNetworks`, and
`supportedWirelessSecurityFlags`.

What it never provides: **access points**. Every report carries an empty `wireless.aps` list, so no
network is ever present or connected as far as the store is concerned. The single access point
visible in a live probe today is WSGM's own synthetic one, injected by `SteamNetworkIndicator` for
the header icon — it is not evidence that Steam enumerates networks on Windows.

So overriding `networkManagementAvailable` yields a Wi-Fi row and an Internet page over an **empty
network list**. The whole list, and scanning, connecting, and forgetting, must come from WSGM's
radio backend through the store's `SetDeviceInfo` ingestion path in the same plain-object shape the
protobuf decoder produces. This is the larger half of the work, not a free ride on the gate.

Two constraints already established on hardware, which must not be rediscovered:

- **Replacing the store's report handler does not work.** The backend holds the bound callback
  registered at store init; a property wrap never fires. Injection goes through the ingestion path.
- **Backend reports expire unknown entries** via each entry's `MarkAsNotPresent()`. Injected access
  points need the same no-op `MarkAsNotPresent` pin `SteamNetworkIndicator` already uses, which
  holds them across reports with no timers and no flicker.

`SetWifiEnabled` exists natively and is **untested** — whether it drives the Windows radio or is
inert is one call, and it is a real radio mutation, so it stays attended.

### Scanning has to follow Steam's surface, not WSGM's panel

Established while wiring the list, and it decides the shape of the remaining work.
`RadioManager.StartScanning`/`StopScanning` are driven by WSGM's own radio panel, and
`RadioManager.Networks` is only fresh while scanning is on. Pushing that collection whenever it
happens to hold something would put a **stale** list in Steam's UI, which is worse than an empty
one: a user picks a network that is no longer there and the join fails for no visible reason.

So the scan lifetime must be driven by Steam's surface. `SteamClient.System.Network` already carries
`StartScanningForNetworks` and `StopScanningForNetworks`, and Steam's own UI calls them when its
network page opens and closes. Intercepting those two calls and starting and stopping
`RadioManager` scanning with them gives exactly the right lifetime — the radio scans while a network
list is on screen and not otherwise, which is also the power-correct answer on a handheld.

That leaves the remaining Wi-Fi work as: intercept the two scanning calls, drive `RadioManager` from
them, and push through `SteamNetworkIndicator.PushNetworksAsync` on each scan result. The push path
itself is built and live-verified; only its trigger is missing.

## Bluetooth: its own service, and fully revivable

Bluetooth does **not** share the SteamOS Manager seam. It is its own WebUI transport service,
`BluetoothManagerService`, whose client stub is the plain object `RF` exported by module `60517`.
The availability the QAM row reads is `is_service_available` from that service's own `GetState`.

The service round-trips on Windows. `GetState({})` returns success, with an empty payload:

```json
{ "is_service_available": false, "adapters": [], "devices": [] }
```

So the transport works and the message shapes are present; only the client-side backend is missing —
the same situation as `SteamClient.System.Perf`, one layer up. The full operation set already
exists: `GetState`, `GetAdapterDetails`, `GetDeviceDetails`, `NotifyStateChanged`, `SetDiscovering`,
`SetLoginAdvertising`, `Pair`, `CancelPair`, `Forget`, `Connect`, `Disconnect`, `SetWakeAllowed`,
`SetTrusted`.

**WSGM replaces the stub's methods** so they resolve locally against the radio helper instead of
calling `SendMsg`. `RF` is a plain object with writable own properties, and the store reaches the
service only through it, so overriding `RF` covers the entire pairing UI. Each replaced method
returns the transport's result shape — `BSuccess()` plus `Body().toObject()` — behind one small
adapter, and `RegisterForNotifyStateChanged` is driven from the radio helper's own device events.

The device payload the UI consumes is `{ id, etype, mac, name, is_paired, is_connected,
strength_raw, battery_percent, should_hide_hint }`, sorted by connected state then signal strength,
and split into paired and available lists.

Implementing `*Handler` as a service registration is **not** available: those are message
descriptors (`{name, request, response}`), not registration hooks. Replacing the stub methods is the
supported route.

This is the largest single piece of work in this plan and should land after Wi-Fi, so the narrower
gate override is proven first. The toggle itself is separate: the row binds the client setting
`system_bluetooth_enabled`, disabled while `is_service_available` is false.

## Audio

Its own plan: `_plan\steam-settings-audio-revive.md`, which covers per-application volume and
speaker configuration in full. In summary it is the cheapest gate in the project — the store's flag
is literally `m_bAvailable = null != SteamClient.System.Audio`, so supplying the namespace is the
whole of it, with no store patching and no getter override — and it runs over
`Shell\AudioManager.cs` and `native\VolumeControl`, which already own devices, volume, mute and
default-endpoint switching.

## Brightness, night mode, resolution

- **Brightness** reuses Valve's row, and the earlier claim here that its availability flag "defaults
  true" was **wrong** — measured on 2026-08-30, `is_display_brightness_available` is explicitly
  `false` in a populated settings message, so the hook's `?? true` never applies and the row is
  hidden. That is a second gate of the same family as the others, and the flag is writable and
  restores cleanly.
  The backend underneath it already works: Steam tracks the real panel brightness
  (`m_flDisplayBrightness` read back `0.806`), and both `SetBrightness` and
  `RegisterForBrightnessChanges` exist on Windows. So this is one flag away, not a transport away.
  If the Steam path turns out not to move the panel, the fallback is the driver: IGCL exports
  `ctlGetBrightnessSetting` and `ctlSetBrightnessSetting`, which makes it a device transport and
  therefore **plugin-owned** under the standing boundary rule, beside Arc Sync.
- **Night mode** is `IN_GAMESCOPE` only, so the row is reused and backed by Windows Night Light.
  This is a WSGM display concern, not a device one.
- **Resolution** appears nowhere in this tab; SteamOS drives it through gamescope. It is added as a
  WSGM row, enumerated from the same runtime mode discovery the frame-limit strategies use.
- **Refresh rate** reuses the Performance tab's component, mounted here, and is shown only when the
  frame-limit strategy is `FrameLimitOnly` — under `NativeModes` or `FrameDoubling` the refresh is
  chosen by the pairing policy and a second control would fight it.

## Ownership

| Concern | Owner |
| --- | --- |
| Panel brightness over the driver | Device Plugin, beside Arc Sync |
| Wi-Fi, Bluetooth, pairing | Existing radio helper |
| Audio devices and volume | Existing native volume helper |
| Night Light, resolution, refresh, mode discovery | WSGM Core display |
| Gate overrides, RPC responses, component mounting | WSGM Steam adapter |

## Implementation slices

1. Override `networkManagementAvailable` and record what the Internet page and Wi-Fi row do unaided.
2. Back whatever remains of Wi-Fi with the radio helper; scan, connect, and forget.
3. Replace the `BluetoothManagerService` stub methods, routing every operation to the radio helper
   and driving `NotifyStateChanged` from its device events.
4. Prove the full pairing lifecycle through Steam's own UI: discover, pair, cancel, connect,
   disconnect, forget, trusted, wake-allowed.
5. Back audio with the volume helper; output device, input device, volume.
6. Brightness over Steam, falling back to plugin-owned IGCL.
7. Night mode over Windows Night Light; resolution and refresh rows from mode discovery.

Each is a separate patch with its own fingerprint, verification, removal, and kill switch. A gate
override that stops matching loses its row and nothing else.

## Validation

Automated: gate-override application and removal; mode discovery and the refresh-row visibility rule
against each frame-limit strategy; state projection for network, Bluetooth, and audio.

Attended, on the reference device: Wi-Fi enumerate, connect, forget, and airplane mode; Bluetooth
pair, connect, disconnect, forget, including a controller over Bluetooth; audio device switching
while a game runs; brightness across both paths; night mode; a resolution change with a game
running; and recovery of every one of these when Steam restarts underneath the patches.