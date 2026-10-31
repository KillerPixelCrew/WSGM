# Radio, Bluetooth and audio integration

What WSGM decides about Wi-Fi, Bluetooth and audio: the wording and policy on top of the Windows
library, the touch-keyboard boundary, and the diagnostics. The Windows mechanisms themselves, radio
power, WLAN scanning and profiles, Bluetooth discovery and pairing, Core Audio endpoints, panel
brightness and the volume cue, live in `external\windows-device-control`, a submodule of
`KillerPixelCrew/windows-device-control`; nothing in that library is specific to WSGM or to gaming,
which is why it was extracted.

Related:

- `external\windows-device-control\docs\radios.md`: the platform constraints and the disproven
  approaches (32feet.NET, the legacy Win32 Bluetooth API, `WiFiAdapter`, the consent store as a
  precondition). Read it before changing how Windows is called.
- [boot and shell](boot-and-shell.md): the Explorer initialization the touch keyboard depends on.

## What WSGM owns

`Shell\Network\RadioManager.cs` is the single owner of radio state for the game-mode UI: radio
power, the Bluetooth device list, scanning demand, the shared status line and the refresh. It
composes two focused parts that the Overlay and Steam reach through it. `WifiController` owns the
Wi-Fi connection state, the network list, the live Wi-Fi watch and the join, disconnect and forget
actions. `BluetoothPairing` owns the one pairing attempt, its questions, cancellation and outcome.
The library returns outcomes; the wording that reaches the user is WSGM's.

The live Wi-Fi and Bluetooth feeds run while the panel or Steam scans and carry every change. The
refresh timer is only a safety net for a driver that stops reporting: it re-reads radio power, the
Wi-Fi status and the connected Bluetooth count every 10 s for the whole session.

`BluetoothDeviceCatalog` keeps Windows watcher endpoint records and merges them by normalized,
non-empty container GUID into the logical collection shared by Overlay and Steam. Missing container
identity falls back to endpoint identity; friendly names never establish identity. A logical row
retains its member endpoint IDs, a paired operation endpoint and a pairable endpoint. Pair/unpair
use those Windows IDs, while UI actions identify the logical row. Removing one transport cannot
disconnect a sibling transport; rescans retire unseen unpaired endpoints and retain paired ones as
offline. Watcher generations reject queued callbacks from a retired discovery session.

Steam Pair opens the shared radio panel before starting Windows pairing, so PIN and confirmation
questions have a visible owner. Busy state is published to Steam's operation spinner. Cancel rejects
the active ceremony; it does not stop discovery or claim that Windows has already finished. Steam
and panel discovery requests are independent, and active pairing retains discovery. Audio
Connect/Disconnect dispatch once and poll endpoint readback for confirmation. Timeout and
unsupported non-audio connection actions report a visible panel error; no optimistic connected state
or automatic write retry is used. Steam transport replies preserve backend failures.

- Only `WifiFailureKind.KeyRejected` and `SecurityMismatch` re-prompt for a password. An unreachable
  network says so instead; re-prompting there makes the user retype a password that was never tried.
- An unusable radio says why (off, blocked by Windows, no adapter, state unavailable) rather than
  collapsing to "Off" and leaving the user pressing a switch that cannot do anything.
- A scan refused with Win32 error 5 names the 24H2 location-consent gate. It reads as a generic
  failure otherwise, and neither elevating nor retrying fixes it.
- `PairingKind.Unknown` is presented as confirm-only rather than declined. An accept is what Windows
  most often wants, and the log records the raw kind so a device that needs another ceremony is
  still diagnosable.

These wording rules are covered by `RadioManagerTests`, `WifiControllerTests` and
`BluetoothPairingTests` in `tests\WSGM.Tests\Shell\Network`; the library contracts they sit on are
tested in the library's own repository.

`Shell\Audio\AudioManager.cs` owns the session's default render and capture endpoint state. The
library's direction-aware `CoreAudio.GetVolume`/`SetVolume` calls are the only Windows edge; WSGM
reads the two directions independently (`AudioSnapshot`) and coalesces each slider's writes
separately (`CoalescingVolumeWrite`). `AudioChangeWatch` owns the Core Audio notifications, with a
10 s safety poll behind them, and `AudioEndpointSelection` serializes each direction's
default-endpoint writes so only the newest choice publishes. The Steam QAM projection does not copy
speaker volume into the microphone field: a missing capture endpoint publishes a null input volume
so Steam leaves that direction unavailable, while a present one receives its own 0-100 value and
routes writes back with `AudioDirection.Capture`.

## Touch keyboard boundary

The radio panel's credential and PIN entry uses WSGM's own `Controls\OnScreenKeyboard` and never
depends on `TabTip.exe`. The Windows touch keyboard itself still depends on Explorer completing its
normal unelevated per-session initialization before game-mode takeover. That shell rule is
documented in [boot and shell](boot-and-shell.md) and is not to be weakened as part of radio work.

## Diagnostics and verification

There is no radio probe mode. Radio state, Wi-Fi and Bluetooth are diagnosed from `wsgm.log` and
exercised through the overlay's radio panel, pairing included.

Compile and isolated tests prove the managed contracts. Power, discovery, pairing ceremonies, audio
reconnection, location consent, and shell-less or elevated behaviour still need device verification
on the reference handheld.

## Source routes

[RadioManager](../src/WSGM/Shell/Network/RadioManager.cs) owns radio power, discovery and the
refresh; [WifiController](../src/WSGM/Shell/Network/WifiController.cs) owns Wi-Fi state and actions;
[BluetoothPairing](../src/WSGM/Shell/Network/BluetoothPairing.cs) owns the pairing attempt and
[PairingPrompt](../src/WSGM/Shell/Network/PairingPrompt.cs) describes its questions;
[RadioEntries](../src/WSGM/Shell/Network/RadioEntries.cs) holds UI-facing entries;
[BluetoothDeviceCatalog](../src/WSGM/Shell/Network/BluetoothDeviceCatalog.cs) merges endpoint
identity and rejects stale discovery callbacks. The pairing attempt belongs to the manager, so
closing one panel does not make another consumer's discovery request disappear.

[AudioManager](../src/WSGM/Shell/Audio/AudioManager.cs) owns live volume/mute and endpoints, with
[AudioEndpointEntry](../src/WSGM/Shell/Audio/AudioEndpointEntry.cs) rows,
[AudioSnapshot](../src/WSGM/Shell/Audio/AudioSnapshot.cs) reads,
[AudioChangeWatch](../src/WSGM/Shell/Audio/AudioChangeWatch.cs) notifications,
[AudioEndpointSelection](../src/WSGM/Shell/Audio/AudioEndpointSelection.cs) and
[EndpointSelectionTracker](../src/WSGM/Shell/Audio/EndpointSelectionTracker.cs) default-device
writes, and [CoalescingVolumeWrite](../src/WSGM/Shell/Audio/CoalescingVolumeWrite.cs) volume writes;
[AudioDiscovery](../src/WSGM/Shell/Audio/AudioDiscovery.cs) coordinates discovery;
[AudioPlaybackChoices](../src/WSGM/Shell/Audio/AudioPlaybackChoices.cs) and
[AudioEndpointText](../src/WSGM/Shell/Audio/AudioEndpointText.cs) project choices and wording.
[NativeQamAudioService](../src/WSGM/Shell/Audio/NativeQamAudioService.cs) and
[NativeQamAudioFormatService](../src/WSGM/Shell/Audio/NativeQamAudioFormatService.cs) expose the
same state to Steam. Saved Game Mode audio policy is owned separately by
[AudioProfileService](../src/WSGM/Shell/Audio/AudioProfileService.cs), described in
[power and display](power-and-display.md#game-mode-audio-profiles).
