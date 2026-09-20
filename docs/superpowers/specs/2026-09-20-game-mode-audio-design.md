# Game Mode Audio Configuration Design

## Goal

Extend Game Mode display switching with optional, crash-safe audio preferences while making channel
layout and spatial sound available as live controls in both WSGM's Overlay and Steam QAM. This
design implements Issue #115 only; the independent Steam UI Toolkit resilience work in Issue #120
begins after this work is complete.

## Scope and user experience

The existing Settings display-profile editor remains the place where the user saves Game Mode and
Desktop preferences. Saving a profile never changes Windows audio. The Overlay and Steam QAM are
live controls only: they operate on the current playback endpoint and show the actual result of the
Windows calls.

Each saved Game Mode or Desktop audio preference may independently leave a value unchanged or
specify:

- default playback endpoint, stored by Core Audio ID with its last friendly name;
- default recording endpoint, stored the same way;
- playback volume and mute state;
- playback default device format, including its channel layout, sample rate and bit depth;
- playback spatial sound format.

Existing configurations deserialize as entirely unchanged preferences. Audio preferences are per
display-switch profile, not per connected display. A newly connected Bluetooth device never
overrides a configured profile; a later mode switch remains the explicit policy boundary.

## State and ownership

`GameModeLaunchConfiguration` owns the saved Game Mode and Desktop audio preferences because it
already owns both directions of the display transition. `GameModeLaunchRecovery` records the audio
snapshot owed to the desktop beside `PendingReturnLayout`; it is runtime state and Settings never
writes it.

A session-owned audio profile service performs all Core Audio reads and writes off the UI thread. It
serializes a profile application and is the single place that maps WindowsDeviceControl results into
an observed outcome. It captures default render and capture endpoints and, for playback, volume,
mute, device format and spatial state. It does not retry a missing endpoint, an uncertain write or a
refused spatial format.

`AudioManager` remains the owner of the live endpoint and volume projections used by the taskbar,
Overlay and existing Steam audio page. It refreshes after the profile service completes, so all
projections converge on one observed state rather than maintaining parallel audio caches.

## Game Mode transition

For a custom Game Mode launch, the entry transaction captures the desktop audio snapshot when it
captures the return layout and persists both snapshots before Explorer exits. After the configured
Game Mode layout has applied and its required displays have settled, it applies the Game Mode audio
preferences. It waits only a short, bounded period for a specified endpoint to enumerate, which
allows an HDMI audio endpoint to appear without creating a retry loop or blocking entry forever.

The apply order is render default endpoint, capture default endpoint, optional playback volume/mute,
optional playback device format, then optional spatial format. Each requested default endpoint
applies all Core Audio roles. Format and spatial writes target the configured render endpoint after
it is selected. A missing endpoint, unsupported format, HRESULT failure, or spatial refusal leaves
that value unchanged, emits a clear status/log outcome and does not fail an otherwise usable Game
Mode transition.

Returning to Desktop restores its configured audio preference when one exists; otherwise it restores
the captured entry snapshot. The return layout is restored before audio so HDMI endpoints can
reappear. Crash recovery uses the same persisted return audio snapshot and clears it only after the
return path has completed.

## Settings profile editing

The existing Game Mode launch editor gains Game Mode and Desktop audio sections. It lists the
currently observed render and capture endpoints and marks an unavailable saved endpoint by its saved
friendly name. Each control explicitly offers `Leave unchanged`; endpoint choices preserve an ID and
name, while format and spatial options are populated only from the selected endpoint's supported
values. The saved device format is an exact supported `AudioDeviceFormat`, which makes channel
configurations such as stereo, 5.1 and 7.1 explicit rather than inferred from a label.

The editor validates only the saved configuration shape. It does not probe, set defaults, change
volume, alter device format or spatial audio while the user edits or saves a profile.

## Live Overlay and Steam QAM controls

The Overlay Audio panel continues to use `AudioManager` and adds live playback controls for
channel/default format and spatial sound. It reads the selected current playback endpoint, shows
only its supported device formats and spatial formats, and displays no selector when that capability
cannot be read. A command is one explicit write followed by an observed refresh; refusals are shown
to the user and logged.

Steam QAM extends the existing native audio surface, rather than adding a second transport or audio
owner. Its closed bridge vocabulary carries only the current playback endpoint's supported formats,
current selections and requests to set an admitted format or spatial value. The WSGM audio adapter
validates every payload against the latest observed endpoint capability before calling the same
audio profile service used by the Overlay. The generated asset is rebuilt from its source fragments
and catalog hash; it is never edited directly.

## Failure handling

All Core Audio access runs off the Avalonia dispatcher. Endpoint absence is informational and
preserves the current Windows default. An HRESULT failure or a `SpatialAudioSetStatus` other than
`Succeeded` is surfaced with the endpoint and requested setting, then requires a new user action or
future mode transition. No automatic retry writes are performed. A readback or `AudioManager`
refresh occurs after a successful write so the UI reports observed state, not an assumed result.

## Testing and documentation

Focused tests cover configuration normalization and migration, profile capture/persistence, entry
and return ordering, endpoint absence, device-format and spatial refusals, crash-return recovery,
payload validation and live Overlay/QAM projection. Tests use fake audio adapters and temporary
configuration roots; they do not change the user's Windows audio or attach to live Steam.

Update `docs/power-and-display.md` for profile behavior and `docs/steam-cef-system.md` for the QAM
bridge vocabulary. Update `_plan/implementation-todo.md` when Issue #115 is complete. An attended
display/HDMI and Steam Big Picture pass remains explicitly reported as unrun until the maintainer
directs it.
