# NVIDIA source and validation record

Implementation date: 2026-10-02. Issue [#177](https://github.com/KillerPixelCrew/WSGM/issues/177).

| Reference                                                                                                                                           | Use                                                                                                 |
| --------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| [NoVidiaApp, 60d93c9c65526214d657743fec3fd7de51c2747d](https://github.com/KillerPixelCrew/NoVidiaApp/tree/60d93c9c65526214d657743fec3fd7de51c2747d) | maintainer's curated settings and individual DRS profile operations                                 |
| [ColorControl, d0d3bb4682c483b0b2c14a794958abf2d142100e](https://github.com/Maassoft/ColorControl/tree/d0d3bb4682c483b0b2c14a794958abf2d142100e)    | display color, dithering and HDR10+ behavior; refreshed upstream checkout under `_ref/ColorControl` |
| [Official NVAPI headers](https://github.com/NVIDIA/nvapi/tree/70d337db9186e968eab622f7e786de7e437faf3d)                                             | documented structures, IDs, values and DRS/color/VRR/HDR contracts in `_ref/nvapi`                  |

The implementation is independent of the reference apps and ships no NVIDIA driver or SDK binary.
ABI facts were taken from the headers. The four local NVAPI headers hash-match that pinned upstream
commit. DWORD-only DRS buffers are 12,320 bytes, values are 414,112 bytes (4,100-byte unions),
application v4 is 20,492 bytes, profile v1 is 4,116 bytes, color v5 is 24 bytes, VRR v1 is 24 bytes
and HDR capabilities v3 is 64 bytes. Color field offsets are format 8, colorimetry 9, range 10, BPC
12, policy 16 and desktop depth 20.

## Private interfaces

Optional bindings live in `NvApiPrivate.cs`, isolated from the documented NVAPI bindings.

| Entry            | QueryInterface ID | Contract from ColorControl                                                            |
| ---------------- | ----------------- | ------------------------------------------------------------------------------------- |
| GetDitherControl | `0x932AC8FB`      | display ID plus 24-byte v1 record: version, state, depth, mode, depth mask, mode mask |
| SetDitherControl | `0xDF0DFCDD`      | current physical GPU, display ID, state, depth, mode                                  |
| GetOutputMode    | `0x81FED88D`      | display ID plus uint32 mode                                                           |
| SetOutputMode    | `0x98E7661A`      | display ID plus uint32 mode: SDR 0, HDR10 1, HDR10+ Gaming 2                          |

These are driver-version dependencies, not promises about every NVIDIA release. Missing functions or
unsupported output queries omit the control. Dither bit/pattern masks are queried. HDR10+ Gaming
requires bit 7 of documented HDR capabilities v3, independently of HDR10+ video bit 6. The HDR10+ ->
HDR10 workaround follows ColorControl and preserves the captured Windows HDR state; it does not
restart the driver.

Private DRS IDs from NoVidiaApp include low latency, Resizable BAR, Vulkan presentation flags,
Smooth Motion, RTX HDR and RTX Digital Vibrance. They require driver-enumerated DWORD values; there
is no guessed-value fallback for them. Driver-reported values absent from the curated label table
are displayed with their actual hexadecimal value.

The low-latency reference was reconciled against ColorControl/NPI: `0x0005F543` is only the Control
Panel's Off/On/Ultra bookkeeping. The package exposes the real ultra-low-latency enable setting
`0x10835000` and the prerender queue independently, rather than presenting a bookkeeping-only write
as a driver effect.

## Evidence and remaining acceptance

Local read-only Windows inventory on 2026-10-02 reports RTX 4070 Laptop GPU, driver `32.0.16.1692`,
alongside Intel UHD Graphics. This records available hybrid hardware only. No NVAPI scenario was
executed and it is not a driver/control acceptance result. No app or GPU test suites were run. The
setup build ran its required native component validation.

Acceptance remains open for global/native-game changes, original-value restoration, external edits,
G-SYNC and non-G-SYNC displays, bit depth/color/dithering/HDR10+ transitions, hotplug, sleep/resume,
driver restart/update and the RTX 5080/Optimus paths. Compilation and package checks do not close
those checks.
