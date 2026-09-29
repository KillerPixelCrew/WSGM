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
  savings) and the per-application sync are built from the header and Intel's samples only.

## What it publishes

Controls are published only when the driver reports them, and only with the values it reports. When
an enum's supported mask is zero, as legacy drivers report, the header's documented members are
offered and a refused write says so.

### Graphics (one section per Intel adapter)

The instance id is the adapter's PCI identity, `pci-8086-<device>-<bus>-<device>-<function>`, or the
enumeration index when the driver does not report the bus address. The LUID is not used: it changes
every boot.

| Capability                                                                                                                                                                                                                                                                               | Kind                   | Mechanism                                         | Scope                                                                       | Timing                                                                   |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------- | ------------------------------------------------- | --------------------------------------------------------------------------- | ------------------------------------------------------------------------ |
| `graphics.<feature>`: frame pacing, frame rate limit, anisotropic filtering, CMAA, texture filtering quality, adaptive tessellation, sharpening, anti-aliasing (MSAA), frame synchronization, emulated 64-bit atomics, low latency, frame generation override, download prebuilt shaders | by reported value type | `ctlGetSet3DFeature` with the reported value type | NativePerApplication when the driver reports per-app support, else Switched | Immediate when the driver reports live change, else NextApplicationStart |
| `graphics.endurance-gaming`, `graphics.endurance-gaming-target`                                                                                                                                                                                                                          | choice                 | feature 1, `ctl_endurance_gaming_t`               | as above                                                                    | as above                                                                 |
| `graphics.adaptive-sync`, `graphics.adaptive-balance`, `graphics.adaptive-balance-strength`, `graphics.adaptive-sync-tearing`                                                                                                                                                            | toggle, range          | feature 10, `ctl_adaptivesync_getset_t`           | as above                                                                    | as above                                                                 |
| `graphics.retro-scaling`                                                                                                                                                                                                                                                                 | choice                 | `ctlGetSetRetroScaling`                           | Switched                                                                    | Immediate                                                                |
| `graphics.shared-memory`                                                                                                                                                                                                                                                                 | range 13-87 %          | registry `GMM\GpuSystemMemoryPinninglimit`        | GlobalOnly                                                                  | SystemRestart                                                            |

A bool feature is a toggle, an enum a choice and an int, uint or float a range. Frame
synchronization's members are flag values; every other enum's mask bit is `1 << value`. A numeric
range whose minimum is above zero gains a zero that means the feature's Enable flag off, which is
how the frame rate limit turns off. A float range is published in the smallest power-of-ten units
that make its step whole. An unknown future feature is published generically as "Intel 3D feature
N". Not published: game compatibility tiers (11, 12), VRR windowed blit (14, reserved), global or
per-app (15), live state (19, read only) and frame generation control (20, undocumented).

### Displays (one section per active display)

The instance id is `edid-<manufacturer><product>-<fingerprint>`, from the EDID codes Windows reports
for the target and an FNV-1a fingerprint of its monitor device path, which Windows keeps for the
same monitor on the same connector across reboots and replugging. The built-in panel's id starts
with `internal-` (`internal-edid-boe0b78-1a2b3c4d`), decided by IGCL's encoder flag
`CTL_ENCODER_CONFIG_FLAG_INTERNAL_DISPLAY`, or by Windows' output technology (internal, embedded
DisplayPort, LVDS, embedded UDI) when IGCL does not answer. WSGM's host gives Valve's single
variable refresh row to an instance starting with `internal`. The section is named after the
monitor, or "Built-in display", and the built-in panel's section sorts first among the displays.

| Capability                                                                                                 | Kind                  | Mechanism                                                   |
| ---------------------------------------------------------------------------------------------------------- | --------------------- | ----------------------------------------------------------- |
| `display.variable-refresh`                                                                                 | toggle                | Arc Sync profile OFF or the last other profile              |
| `display.arc-sync-profile`                                                                                 | choice                | Recommended, Excellent, Good, Compatible, VESA              |
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
is off, and a Custom profile reads as unknown.

Colour follows Intel's colour sample: brightness (-25 to 25), contrast (75-125 %) and gamma (80-130,
the curve exponent is 100 divided by it) become one uniformly sampled 1D LUT on the pipe's last 1D
LUT block; hue (0-359) and saturation (75-125 %) become a BT.709 CSC on its first matrix block.
Neutral values write an exact identity. Every write persists across power events. Colour is not
offered on an HDR output. The driver hands back the LUT, not the settings, so the plugin records
what it wrote in `color.v1.json` and recognises the driver's current LUT only when it is the
identity or matches that record; anything else reads as unknown, and writing one value of a block
then resets the block's other values to neutral.

Low refresh rate that needs panel self refresh off (`bRequirePSRDisable`) turns PSR off for that
power source, writes, and turns it back on if it was on.

## Writes and readback

Nothing is gated on a readback. A write goes to the driver, is read back, and is reported
`AppliedVerified` when the readback matches and `AppliedUnverified` when it does not. The written
value is published as observed for the rest of the cycle, until the driver reports a value other
than the one it reported right after the write. A write the driver refused is `Rejected`, a failed
one `Indeterminate`, and neither is retried or rolled back.

## Per-application profiles

For a capability published as NativePerApplication, WSGM writes the Global value through commands
and hands every game's overrides to `SyncApplicationProfilesAsync`. The plugin writes them with
`ctlGetSet3DFeature` and the executable's file name. IGCL has no delete, so each write is bracketed
by a listing of the adapter's `3DKeys` value names starting with `<exe>_`, and the names that
appeared are kept in `application-profiles.v1.json` in the state directory. When an override is no
longer wanted, exactly those recorded names are deleted, never a name that was already there or one
another override still holds. A sync older than the last one applied is skipped. A delete that fails
stays in the record and is attempted once in the next sync, which re-derives everything; nothing is
retried within one sync.

## Lifecycle

One IGCL session per cycle: opened at start and on resume, reopened after
`CTL_RESULT_ERROR_DEVICE_LOST`, closed on suspend and stop. All driver calls run on one lane. With
no `ControlLib.dll` or no Intel adapter the plugin publishes an empty descriptor set and Unavailable
health. The driver's state is read every 10 seconds and republished when it changes, or before
WSGM's freshness window runs out.

## Build

```powershell
dotnet build src/WSGM.Plugin.IntelGpu/WSGM.Plugin.IntelGpu.csproj -c Release
dotnet test tests/WSGM.Plugin.IntelGpu.Tests/WSGM.Plugin.IntelGpu.Tests.csproj
```
