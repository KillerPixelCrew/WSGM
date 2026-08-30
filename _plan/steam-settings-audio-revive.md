# WSGM 2.0 Steam audio settings revival

Status: implementation plan, written 2026-08-30

Evidence: live-probed against the local Windows Steam client on its Audio settings page, and
measured on the reference MSI Claw 8 AI+ A2VM. Probes retained as
`tools\WsgmLibTest\probe-audio.js` and `probe-speaker-config.js`.

## Read this first

WSGM already owns this backend. Nothing below is "implement audio"; it is an adapter plus one new
capability.

| Existing | What it already does |
| --- | --- |
| `Shell\AudioManager.cs` | Output and input endpoint lists, selection, volume, mute, reconcile |
| `Interop\NativeVolumeControl.cs` | `WsgmVolumeGet/Set/Command`, `WsgmAudioListEndpoints`, `WsgmAudioSetDefaultEndpoint`, feedback |
| `native\VolumeControl\VolumeControl.cpp` | The COM side, and **already declares `IPolicyConfig` including `SetDeviceFormat`**, using `SetDefaultEndpoint` today |
| `Shell\VolumeButtonService.cs` | Volume button handling, which Steam expects as an event |
| `Shell\VolumeAppCommands.cs` | Media-key command decoding — **not** a mixer |
| `Overlay\AudioWindow.axaml` | The existing WSGM audio surface |

## The gate is the cheapest one in the project

The store's availability flag is literally:

```js
this.m_bAvailable = null != SteamClient.System.Audio;
```

`SteamClient.System.Audio` is undefined on Windows. Supplying the namespace is the entire gate — no
store patching, no getter override, no platform constant. Same category as
`SteamClient.System.Perf`.

| Method the store binds | Backed by |
| --- | --- |
| `GetDevices()` → `{activeOutputDeviceId, …}` | `WsgmAudioListEndpoints` both flows, `WsgmVolumeGet` |
| `SetDefaultDeviceOverride(id, direction)` | `WsgmAudioSetDefaultEndpoint` |
| `SetDeviceVolume(id, volume, direction)` | `WsgmVolumeSet` |
| `RegisterForDeviceAdded` / `RegisterForDeviceRemoved` | `AudioManager` endpoint reconcile |
| `RegisterForDeviceVolumeChanged` | `AudioManager` volume state |
| `RegisterForVolumeButtonPressed` | `VolumeButtonService` |
| `RegisterForServiceConnectionStateChanges` | constant ready |
| `SetAppVolume`, `RegisterForAppAdded`, `RegisterForAppRemoved` | new — see below |

The device model needs `HasDirection(Input\|Output)`, per-direction volumes, and an original name,
which `AudioEndpointEntry` already carries per flow.

### The exact payload shapes, live-probed 2026-08-30

Guessing these would produce a namespace that exists and feeds the store nonsense, which is worse
than leaving it absent, so they were read off the store's own consumers rather than inferred.

`GetDevices()` resolves to:

```
{ activeOutputDeviceId, activeInputDeviceId, overrideOutputDeviceId, overrideInputDeviceId,
  vecDevices: [ device, … ] }
```

Each `device` is read by the store's `RegisterOrUpdateDevice` into its device class:

```
{ id, sName, bHasOutput, bHasInput, currentConfig, availableConfigs,
  eConnectorType, eBus, bSupportsHdmiCec, bHdmiCecEnabled, bHdmiCecActive }
```

`currentConfig` and `availableConfigs` belong to speaker configuration and the three HDMI CEC fields
to a service WSGM does not supply, so those are reported empty and false rather than invented — the
affected controls then do not appear, which is the intended outcome.

`GetApps()` resolves to `{ rgApps: [{ id, strName, flVolume, unPID }] }`. Until the WASAPI session
mixer exists this returns an empty list, which is why Steam's per-app mixer shows nothing rather than
misbehaving.

`UpdateDefaultDevices` re-reads `GetDevices()` after every device add or removal, so the response
must stay cheap: it is called on hardware churn, not once at startup.

## Per-application volume

WSGM has no per-app session control. This is wanted for the custom taskbar independently of Steam,
so it is **one backend serving two surfaces**: WASAPI session enumeration and per-session volume in
`native\VolumeControl` beside the COM already there, surfaced through `AudioManager`, consumed by
the taskbar and the Steam adapter alike.

Until it lands, `GetDevices` reports no apps and Steam's per-app mixer lists nothing rather than
misbehaving.

## Speaker configuration

Wanted because Windows genuinely loses this across display changes, when HDMI endpoints are
re-enumerated. The feature is therefore **remember the chosen configuration per endpoint and reapply
it when endpoints churn**, with the dropdown as the way to choose it. That is the same shape as
WSGM's display profiles, and the same shape DisplayMagician uses for audio device switching with a
revert on exit.

Steam's half is ready: the audio module carries `CAudio_SetSpeakerConfiguration_Request`
(`sink_id`, `config`) returning `config`, `channels`, `sdescription`, plus
`CAudio_PlaySpeakerTestOnChannel` for per-channel test tones. It is an RPC stub, so it is the same
method-replacement shape as `BluetoothManagerService`.

**The mechanism is `IPolicyConfig::SetDeviceFormat(deviceId, endpointFormat, mixFormat)`**, passing
a `WAVEFORMATEXTENSIBLE` whose `dwChannelMask` carries the stereo, 5.1, or 7.1 layout. This is the
interface the Windows Sound control panel itself uses, and `VolumeControl.cpp` already declares it
with the correct vtable ordering — `SetDeviceFormat` is declared immediately after
`ResetDeviceFormat` — and already calls `SetDefaultEndpoint` through it in production. The risky
part of an undocumented interface, getting the vtable right, is therefore already done and proven.

### Rejected, with evidence

Writing `PKEY_AudioEndpoint_PhysicalSpeakers` or `PKEY_AudioEngine_DeviceFormat` through
`IPropertyStore::SetValue`. The endpoint property store does open `STGM_READWRITE` on the reference
unit, so it looks available, but Microsoft documents these as set by the Windows audio service with
clients expected to read and not write them, and changing the device format this way is reported not
to take effect. `IPolicyConfig` is the route that works.

### Unknowns that must be closed before building it

- **Multichannel is unproven.** The reference Claw exposes one stereo Realtek endpoint, 2 channels,
  mask `0x3`, with `PhysicalSpeakers` absent — and absent on all six persisted render endpoints in
  `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render`. Neither reading nor
  writing a 5.1 or 7.1 configuration has been demonstrated. Closing this needs an HDMI receiver, a
  TV, or a USB DAC attached; no such hardware is currently available.
- **Endpoint identity across a display change is unproven.** Whether a re-enumerated HDMI endpoint
  keeps its id decides whether reapply-on-churn can key on the endpoint id or needs a fuzzier match
  on name and container. This is the crux of the actual feature and is settled by one display change
  with a multichannel endpoint attached.

HDMI CEC (`SetHdmiCecEnabled`, `SendHdmiCecVolume`) reaches the same service and stays unsupported;
those controls simply do not appear.

## The audio manager's lifetime is the blocker

Found while wiring the session host, and it is a real constraint rather than an oversight in the
plan: **`AudioManager` is taskbar-scoped, not session-scoped.** `OverlayController` creates
`SystemStatus` — which owns the manager — when the taskbar opens, and disposes it when the taskbar
closes. Steam's audio namespace has to answer for the whole session, including while the taskbar is
shut.

Two ways out, and only one of them is acceptable:

- **Hoist the manager to session scope** and hand the same instance to both the taskbar's status
  cluster and the Steam host. This is the correct fix and is a lifetime change to
  `SystemStatus`/`OverlayController` ownership, not to the audio code itself.
- **Give the Steam host its own manager.** Rejected: two managers mean two endpoint enumerations and
  two ideas of which device is default, which is exactly the disagreement the adapter exists to
  prevent. It would also double the WASAPI work on every device change.

Until the first is done, `SteamUiSessionHost` takes an optional manager, nothing passes one, and the
audio patch is simply not registered — so Steam's store stays unavailable and no half-supplied
namespace exists. That is the honest degradation, but it does mean audio is **built and unreachable**
rather than working.

## Implementation slices

1. Supply `SteamClient.System.Audio` over `AudioManager`, devices and volume and mute only.
2. WASAPI session enumeration and per-session volume in `native\VolumeControl`, surfaced through
   `AudioManager`, consumed by the taskbar and Steam.
3. Close the two speaker-configuration unknowns on real multichannel hardware.
4. `SetDeviceFormat` through the already-declared `IPolicyConfig`, with the chosen configuration
   persisted per endpoint and reapplied on endpoint churn.
5. Replace the `CAudio_SetSpeakerConfiguration` and `CAudio_PlaySpeakerTestOnChannel` stubs.

Each is a separate patch with its own fingerprint, verification, removal, and kill switch.

## Validation

Automated: namespace shape and device projection; volume and mute round trips through the existing
seams; channel-mask to configuration mapping as a pure decision test; persistence and reapply
decisions against synthetic endpoint-churn sequences.

Attended: device switching while a game runs; volume buttons; per-app volume against a real mixer;
and, once multichannel hardware exists, setting 5.1 and 7.1, the per-channel speaker test, and
surviving a display change with the configuration restored.
