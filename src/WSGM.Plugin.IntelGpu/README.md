# WSGM Intel Graphics plugin

A `wsgm.gpu` package for Intel graphics. It runs beside the device package, with or without device
integration, and publishes Intel's driver settings as WSGM capabilities: the 3D features, the shared
GPU memory split and retro scaling of every Intel adapter, and the refresh, picture, colour and
power settings of every active display an Intel adapter drives. It also keeps the driver's own
per-application profiles in line with WSGM's game profiles. MIT-licensed, like the device packages.

Everything goes through Intel's Graphics Control Library (IGCL) in the driver's own
`ControlLib.dll`, except the shared GPU memory split, which the driver only keeps in the registry.
IGCL is preferred wherever it has the setting, because a registry value only takes hold after a
restart. Nothing Intel ships is vendored.

## Status

The plugin itself has not yet run on hardware. The call sequences, structure layouts and value
encodings it relies on were measured with a probe on 2026-09-29 on an Intel UHD laptop (8086:4688,
driver 32.0.101.7088), and the Arc Sync, Endurance Gaming, shader download and memory paths are
carried over from the Claw package, which exercised them on the Claw 8 AI+ A2VM. `PROVENANCE.md` has
the details and dates. What is blind:

- Frame synchronization (feature 9) is written through IGCL, not `Global_AsyncFlipMode`. The
  laptop's legacy driver refused the write with `CTL_RESULT_ERROR_UNSUPPORTED_FEATURE`; the Claw's
  earlier "IGCL cannot set feature 9" finding almost certainly came from reading the enum at the
  wrong offset. This awaits Claw evidence.
- Every display control (scaling, sharpness, colour, wire format, end-display settings, power
  savings), the Arc Sync Custom profile and the per-application sync are built from the header and
  Intel's samples only.
- The game profile tiers (feature 11), VRR windowed blit (14), the per-application switch (15) and
  the live state (19) are built from the header and `3D_Feature_Sample_App.cpp`. The laptop's table
  listed feature 11 as custom-typed and feature 15; none of these has been read or written by the
  plugin yet.

## What it publishes

Controls are published only when the driver reports them, and only with the values it reports. When
an enum's supported mask is zero, as legacy drivers report, the header's documented members are
offered and a refused write says so.

### Graphics (one section per Intel adapter)

The instance id is the adapter's PCI identity, `pci-8086-<device>-<bus>-<device>-<function>`, or the
enumeration index when the driver does not report the bus address. The LUID is not used: it changes
every boot.

| Capability                                                                                                                                                                                                                                                                                                                   | Kind                   | Mechanism                                              | Scope                                                                       | Timing                                                                   |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------- | ------------------------------------------------------ | --------------------------------------------------------------------------- | ------------------------------------------------------------------------ |
| `graphics.<feature>`: frame pacing, frame rate limit, anisotropic filtering, CMAA, texture filtering quality, adaptive tessellation, sharpening, anti-aliasing (MSAA), frame synchronization, emulated 64-bit atomics, variable refresh in windowed games, low latency, frame generation override, download prebuilt shaders | by reported value type | `ctlGetSet3DFeature` with the reported value type      | NativePerApplication when the driver reports per-app support, else Switched | Immediate when the driver reports live change, else NextApplicationStart |
| `graphics.compatibility-profile`, `graphics.performance-profile`                                                                                                                                                                                                                                                             | choice                 | feature 11, `ctl_3d_app_profiles_t`, one per tier type | as above                                                                    | as above                                                                 |
| `graphics.live-api`, `graphics.live-target-fps`, `graphics.live-frame-pacing`                                                                                                                                                                                                                                                | read only              | feature 19, `ctl_3d_live_state_t`                      | GlobalOnly                                                                  | read every 10 seconds                                                    |
| `graphics.endurance-gaming`, `graphics.endurance-gaming-target`                                                                                                                                                                                                                                                              | choice                 | feature 1, `ctl_endurance_gaming_t`                    | as above                                                                    | as above                                                                 |
| `graphics.adaptive-sync`, `graphics.adaptive-balance`, `graphics.adaptive-balance-strength`, `graphics.adaptive-sync-tearing`                                                                                                                                                                                                | toggle, range          | feature 10, `ctl_adaptivesync_getset_t`                | as above                                                                    | as above                                                                 |
| `graphics.retro-scaling`                                                                                                                                                                                                                                                                                                     | choice                 | `ctlGetSetRetroScaling`                                | Switched                                                                    | Immediate                                                                |
| `graphics.shared-memory`                                                                                                                                                                                                                                                                                                     | range 13-87 %          | registry `GMM\GpuSystemMemoryPinninglimit`             | GlobalOnly                                                                  | SystemRestart                                                            |

A bool feature is a toggle, an enum a choice and an int, uint or float a range. Frame
synchronization's members are flag values; every other enum's mask bit is `1 << value`. A numeric
range whose minimum is above zero gains a zero that means the feature's Enable flag off, which is
how the frame rate limit turns off. A float range is published in the smallest power-of-ten units
that make its step whole. An unknown future feature is published generically as "Intel 3D feature
N".

The game profiles (feature 11) are Intel's game compatibility and performance tiers. Each tier type
the driver reports in `ctl_3d_app_profiles_caps_t.SupportedTierTypes` is its own value, asked for
through the structure's `TierType` input, so each becomes its own choice: Off, and the tiers the
driver reports in `SupportedTierProfiles` (Tier 1, Tier 2, Recommended), all three when that mask is
zero. Nothing stored reads as `DefaultEnabledTierProfiles`. Several tiers enabled at once, which the
flags type allows, reads as unknown. Every get and set carries the custom value type and a 32-byte
buffer. VRR windowed blit (feature 14) is published as Auto, On and Off when the driver lists it.
Those are the members `ctl_3d_vrr_windowed_blt_reserved_t` documents (lines 1826-1834), although the
header calls the functionality reserved, so a refusal is reported as it comes.

The live state (feature 19) is three read-only rows in a Live status category: the active graphics
API (DirectX 9, 11, 12, Vulkan, several or none, from the misc flags; the live-change bit is not an
API), the frame pacing target in frames per second (shown up to 1000), and whether frame pacing is
off, on and active, or on but not active. They are read in the observation pass, published only when
they change, and traced through `TraceChange`.

What the header does not allow:

- Feature 12, game profile customisation: `ctl_3d_tier_details_t` (`igcl_api.h` lines 1806-1812)
  holds only its `TierType` and `TierProfile` inputs and reserved space, and
  `CustomizationSupportedTierProfiles` is "reserved for future" (line 1795). There is nothing to
  read or set.
- Feature 15, global or per-app (lines 1531, 1837-1845): not a row. Intel's sample writes
  `CTL_3D_GLOBAL_OR_PER_APP_TYPES_PER_APP` for an executable and notes that a per-application value
  applies only once that is set, so the per-application sync writes it for every executable it
  stores overrides for (see below). A global value has no meaning.
- Feature 20, frame generation control (line 1536): declared with no value type, enum or structure,
  so there is nothing to map. The table's entry is logged and skipped.
- The live state's `GfxApi` is a set of flags (line 1990); it is published as one choice, so two
  APIs at once read as "Several".

### Displays (one section per active display)

The instance id is `edid-<manufacturer><product>-<fingerprint>`, from the EDID codes Windows reports
for the target and an FNV-1a fingerprint of its monitor device path, which Windows keeps for the
same monitor on the same connector across reboots and replugging. The built-in panel's id starts
with `internal-` (`internal-edid-boe0b78-1a2b3c4d`), decided by IGCL's encoder flag
`CTL_ENCODER_CONFIG_FLAG_INTERNAL_DISPLAY`, or by Windows' output technology (internal, embedded
DisplayPort, LVDS, embedded UDI) when IGCL does not answer. WSGM's host gives Valve's single
variable refresh row to an instance starting with `internal`. The section is named after the
monitor, or "Built-in display", and the built-in panel's section sorts first among the displays. The
active displays are enumerated again on every observation pass, so a monitor connected or
disconnected while WSGM runs gains or loses its section within 10 seconds.

The adapters come first, then the built-in panel, then the other displays in order. Every active
display gets its section; WSGM sets no limit on how many controls or sections one plugin publishes.

| Capability                                                                                                 | Kind                  | Mechanism                                                   |
| ---------------------------------------------------------------------------------------------------------- | --------------------- | ----------------------------------------------------------- |
| `display.variable-refresh`                                                                                 | toggle                | Arc Sync profile OFF or the last other profile              |
| `display.arc-sync-profile`                                                                                 | choice                | Recommended, Excellent, Good, Compatible, VESA, Custom      |
| `display.arc-sync-min-refresh`, `-max-refresh`, `-frame-time-increase`, `-frame-time-decrease`             | range                 | the Custom profile's `ctl_intel_arc_sync_profile_params_t`  |
| `display.scaling`, `display.scaling-width`, `display.scaling-height`                                       | choice, ranges        | `ctlGet/SetCurrentScaling`                                  |
| `display.sharpening`, `display.sharpening-filter`, `display.sharpening-intensity`                          | toggle, choice, range | `ctlGet/SetCurrentSharpness`                                |
| `display.wire-format`                                                                                      | choice                | `ctlGetSetWireFormat`                                       |
| `display.quantization-range`, `display.content-type`, `display.low-latency`, `display.source-tone-mapping` | choice                | `ctlGetSetDisplaySettings`, supported and controllable only |
| `display.color-brightness`, `-contrast`, `-gamma`, `-hue`, `-saturation`                                   | range                 | `ctlPixelTransformationSetConfig`, 1D LUT and CSC           |
| `display.psr.plugged-in`, `display.psr.battery`, `display.fbc.*`, `display.lrr.*`                          | toggle, choice        | `ctlGet/SetPowerOptimizationSetting` per power source       |
| `display.dpst`, `display.dpst-level`                                                                       | toggle, range         | the same, on battery only                                   |
| `display.lace`, `display.lace-level`                                                                       | toggle, range         | `ctlGet/SetLACEConfig`                                      |

Every display control is Switched and Immediate. Variable refresh carries the `VariableRefreshRate`
role on one display, the built-in panel when it has variable refresh, otherwise the first display
that does; any other display publishes it as a generic toggle. Enabling restores the last profile
other than OFF, falling back to Recommended. The profile row is unavailable while variable refresh
is off.

Custom is offered when the monitor's Arc Sync range holds at least two whole refresh rates. Its four
rows are the minimum and maximum refresh in Hz, within the range `ctlGetIntelArcSyncInfoForMonitor`
reports, and the maximum frame time increase and decrease in microseconds, from zero to the frame
time span of that range (or the monitor's own reported limit when larger). They always show what the
driver applies, and are available only while the profile is Custom and variable refresh is on.
Choosing Custom starts from the last Custom values seen, else from what the driver applies for the
current profile. Writing one row keeps the other three and sets the Custom profile; a minimum at or
above the maximum is refused before it reaches the driver.

Colour follows Intel's colour sample: brightness (-25 to 25), contrast (75-125 %) and gamma (80-130,
the curve exponent is 100 divided by it) become one uniformly sampled 1D LUT on the pipe's last 1D
LUT block; hue (0-359) and saturation (75-125 %) become a BT.709 CSC on its first matrix block.
Neutral values write an exact identity. Every write persists across power events. Colour is not
offered on an HDR output. The driver hands back the LUT, not the settings, so the plugin records
what it wrote in `color.v1.json` and recognises the driver's current LUT only when it is the
identity or matches that record, and the matrix the same way. A block holding anything else, such as
another tool's curve, publishes its values (brightness, contrast and gamma for the LUT, hue and
saturation for the matrix) as unknown rather than guessing. Writing one value of such a block
replaces it: the block's other values start from neutral, and the log says so at that write. Each
block is judged on its own, so a foreign LUT beside the identity matrix leaves hue and saturation
known.

Low refresh rate that needs panel self refresh off (`bRequirePSRDisable`) turns PSR off for that
power source, writes, and turns it back on if it was on.

The custom scaling sizes are unavailable while another scaling is selected, and the LACE strength
while LACE follows the ambient light sensor.

## Writes and readback

Nothing is gated on a readback. A write goes to the driver, is read back, and is reported
`AppliedVerified` when the readback matches and `AppliedUnverified` when it does not. The written
value is published as observed for the rest of the cycle, until the driver reports a value other
than the one it reported right after the write. A write the driver refused is `Rejected`, a failed
one `Indeterminate`, and neither is retried or rolled back. A read-only row refuses every write, and
a value off a row's range or step, or a choice it does not offer, is refused before it reaches the
driver. A driver value between two steps is published as the nearest step.

Controls that share one driver structure (sharpness on, filter and intensity, for example) share one
read of it per observation pass, and a command reads it afresh before and after its write.

## Per-application profiles

For a capability published as NativePerApplication, WSGM writes the Global value through commands
and hands every game's overrides to `SyncApplicationProfilesAsync`. The plugin writes them with
`ctlGetSet3DFeature` and the executable's file name. IGCL has no delete, so each write is bracketed
by a listing of the adapter's `3DKeys` value names starting with `<exe>_`, and the names that
appeared are kept in `application-profiles.v1.json` in the state directory, saved after every write
and every removal so a sync cut short still remembers what it created. The names belong to the
executable, the capability and the adapter, not to the game profile, so a profile that takes over
another's executable takes over its names. When an override is no longer wanted, exactly those
recorded names are deleted, never a name that was already there or one another override still holds.
An override whose recorded value is unchanged and whose recorded names are all still there is
confirmed without writing it again. A sync older than the last one applied in the same WSGM run is
skipped; WSGM's revision starts again with WSGM, so it is never compared across runs. A delete that
fails stays in the record and is attempted once in the next sync, which re-derives everything;
nothing is retried within one sync.

When the adapter lists feature 15 as an enum with per-app support, every executable with an override
also gets `CTL_3D_GLOBAL_OR_PER_APP_TYPES_PER_APP` on that adapter, written once per sync before its
overrides and bracketed the same way. Its names are recorded as their own entry
(`graphics.per-application`), kept while any override for that executable is wanted and deleted with
the last one, which returns the executable to the driver's own choice.

## Lifecycle

One IGCL session per cycle: opened at start and on resume, reopened after
`CTL_RESULT_ERROR_DEVICE_LOST` or `CTL_RESULT_ERROR_UNINITIALIZED`, closed on suspend and stop. Any
other failed call fails only its own control for that pass. All driver calls run on one lane. With
no `ControlLib.dll` or no Intel adapter the plugin publishes an empty descriptor set and Unavailable
health, and asks again after 10 seconds, then less and less often, up to every 5 minutes; health is
published and logged only when it changes. Start publishes the descriptors and returns; the first
observation follows at once. The driver's state is read every 10 seconds and republished when it
changes, or before WSGM's freshness window runs out.

Stop waits up to 5 seconds for the observation loop and 5 more for the lane. When a driver call is
still running after that, stop reports itself unconfirmed and leaves the session and
`ControlLib.dll` open; the call's thread closes them when it returns.

## Build

Unsupported features are omitted from descriptor publications, including features explicitly
rejected by the driver during observation or a write. Temporary transport failures and prerequisites
do not remove controls. Suppression lasts for the capability cycle and is retained across output
rebuilds. FBC is not offered as a writable toggle: its hardware capability/readable state does not
establish setter support, and the observed driver explicitly rejects setting it.

```powershell
dotnet build src/WSGM.Plugin.IntelGpu/WSGM.Plugin.IntelGpu.csproj -c Release
dotnet test tests/WSGM.Plugin.IntelGpu.Tests/WSGM.Plugin.IntelGpu.Tests.csproj
```
