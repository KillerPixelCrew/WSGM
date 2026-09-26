# Profiles

What a Global and per-game profile are, how a value resolves, which surfaces write where, and what
Steam's per-game toggle and reset do. The store is `AppConfig.Profiles` (`Core\Profiles\`); the one
owner is `Shell\ProfileService`.

## A game profile holds only what was changed for that game

A per-game profile starts empty. Every value in it is one the user set while that profile was on.
Everything it does not set is read from Global each time it is resolved and is never copied in.
Changing Global therefore reaches every game that does not override that value.

Opting in used to seed the game from the values in force, which froze Global into it; later Global
changes never reached that game. Nothing does that now.

## Every value resolves the same way

For each setting on its own: the running game's enabled profile, then Global, then nothing. Nothing
means WSGM writes nothing and the device, RTSS or display keeps what it has.

"Each setting on its own" includes the four manual power values (`TdpUnified`, `UnifiedWatts`,
`SustainedWatts`, `BoostWatts`) and every lighting zone. A game that sets only its mode keeps every
Global wattage. A game that sets only its button colour keeps the Global ring colours.

`ProfileLayers` in `Core\Profiles\ProfileResolver.cs` is the resolver, and every consumer uses it.

| Setting                               | Consumer                                                                    |
| ------------------------------------- | --------------------------------------------------------------------------- |
| `FrameLimit`, `OverlayLevel`          | `PerformanceService` (RTSS)                                                 |
| manual power values                   | `ApplicationPerformanceReconciler`, the coordinator's power pair            |
| `VariableRefreshRate`                 | `ApplicationPerformanceReconciler`                                          |
| `CpuBoost`                            | `ApplicationPerformanceReconciler` (Windows processor boost mode)           |
| `AcPowerPreset`, `BatteryPowerPreset` | `DevicePowerAssignments`                                                    |
| `FanCurveProfileId`                   | `DeviceCoordinator.ApplyAuthoredProfilesAsync`                              |
| `ControllerTarget`                    | `ControllerTargetSelection` (default `SteamDeckComposite`)                  |
| `Device[]` (capability, instance)     | `DeviceCapabilityRouter` desired state, keyed by the machine's identity key |

## One running application, one profile

`RunningApplicationCoordinator` hands the running application to `ProfileService`, which matches it
once (`ApplicationProfileRules.Match`: executable rules first, then the canonical id of a profile
with none) and publishes a `ProfileSnapshot`. RTSS, the device, power, refresh and the controller
target all resolve from that snapshot, so they cannot disagree about which game is running or which
layer is in force.

`ProfileFanOut` carries each change to the consumers in a fixed order: RTSS, then the device
(desired values, fan profile, controller target), then power and refresh. A different running
application cancels the pass in progress; a value change waits behind it.

## Where an edit lands

A running game with its profile on: the game. Anything else: Global. This is the same for the
overlay rows, the Quick Access rows and the manual power and refresh funnels. An edit meant for a
game profile that another process has since deleted is refused rather than written to Global.

Settings edits Global only: the controller target and the authored fan-curve library. Deleting an
authored profile clears every layer that selected it, so that layer falls back instead of naming
nothing; normalization also drops a reference to a profile that no longer exists.

## Steam's toggle and reset

"Use per-game profile", the overlay header's Global / Per-application and the Device root's
Per-application row are one switch: `ProfileService.SetGameEnabledAsync`. The first switch-on
creates the empty profile, bound to the canonical id and the running executable. Switching off keeps
its values, so switching back on restores them.

"Reset to default" clears every value in the game profile when it is on, so the game inherits
everything and keeps its binding. When it is off it clears only the Performance tab's Global values:
frame limit, overlay level, the manual power values, VRR and the processor boost mode. It never
reaches lighting or other device values.

A game without a Steam AppID still gets its profile. Valve's header needs an AppID, so there it
reads as global; WSGM's own rows still show the game layer.

## A value the game overrides is marked

Every row whose value the running game's profile supplies says so, in the overlay and in the Quick
Access rows WSGM owns. Nothing is drawn for a value from Global or from nowhere, so a game without
overrides looks exactly as it would with no profile.

| Surface                         | Marker                                       | Way back to Global                          |
| ------------------------------- | -------------------------------------------- | ------------------------------------------- |
| Overlay device and host rows    | accent bar and "Game override" under the row | Use global button (`ProfileOverrideMarker`) |
| Overlay frame limit and overlay | the same                                     | Use global                                  |
| Overlay power presets           | "Game override" beside the source's title    | the "Use global assignment" entry           |
| Overlay manual power mode       | accent bar and "Game override"               | Use global                                  |
| Overlay header                  | "Profile: name · N overrides"                | Reset performance profile                   |
| Quick Access rows WSGM owns     | "Game override" description in Steam blue    | Steam's Reset button                        |
| Quick Access power presets      | the same                                     | the unset entry                             |

The overlay's Use global calls `ProfileService.ClearGameOverrideAsync`, which removes only the
game's value; the fan-out then applies Global and the marker disappears. It never clears Global.
Each marker carries the setting's id (`ProfileSettingKey.Id`: a field name, or
`device:<capability>#<instance>`). Quick Access only colours the row's description while it has one
and offers no per-row control; the maintainer found a button under every overridden row too heavy
for Steam's panel (2026-09-25).

Valve's own overlay-level selector draws no marker: it reads Valve's store, which has no field for
it. WSGM's overlay row for the same value does.

## Wake and new cycles

Firmware can come back from sleep with its own defaults. Each time the device cycle becomes active,
including after resume, every desired value is restored once, from whichever layer supplies it. The
power preset waits for that pass instead of competing with it for the plugin's command lane.

Lighting is also restored from readiness publications, bounded by `DeviceLightingRestore`: at most
three attempts per zone, value and cycle. A refused command wrote nothing and may be tried again. An
uncertain one (`TimedOut`, `Indeterminate`) waits until a readback taken after it shows the zone
does not hold the value. That readback is the re-read the no-blind-retry rule asks for.

Each lighting restore logs one line per zone:

    Device restore lighting.zone-color/left-ring from Global (device Active): outcome=AppliedVerified, attempt 1 of 3.

## Stored shape

`Profiles.Global` and `Profiles.Games[]` (`Id`, `Name`, `ProcessNames`, `Enabled`, `Values`). Every
member of `Values` is optional. Normalization trims ids and names, drops a second profile with an id
already used, drops an executable already claimed by an earlier profile, validates preset
references, masks colours to 24 bits and drops device values with no key.

The retired model (per-application entries under `Performance`, `DeviceIntegration.Profiles`, the
AC, DC and hardware-profile layers, per-application controller targets and authored-profile
selections) is wiped by `ConfigMigrations` on the first load; its OEM assignments move to
`DeviceIntegration.OemAssignments`.
