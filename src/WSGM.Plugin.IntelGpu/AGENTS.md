# Intel Graphics plugin contributor instructions

## Scope and sources of truth

These instructions apply to `src/WSGM.Plugin.IntelGpu/**` and its tests. The package is the MIT-licensed
`wsgm.gpu` plugin for Intel graphics: 3D features, the shared GPU memory override, retro scaling and the display
settings of every active Intel-driven output. Read `README.md`, `PROVENANCE.md` and the tests before changing
behaviour.

- Intel's `igcl_api.h` and samples in `_ref/intel-gcl` are the reference for every structure, value and call
  sequence. A layout comes from the header, never from inference, and every mirrored size or offset is pinned in
  `NativeLayoutTests`.
- A dated observation in `PROVENANCE.md` is evidence from that machine and day. Say which machine (the laptop's UHD
  8086:4688 or the Claw) a claim rests on, and never present a blind path as tested.
- Never hard-code a device, OEM or model check. What the driver reports decides what is offered.

## Driver access

- IGCL is loaded from the driver's own `ControlLib.dll` through `NativeLibrary` and unmanaged function pointers.
  Nothing Intel ships is vendored or redistributed.
- `ctlEnumerateDevices` is always called with a zero count and a null array first, then with the array. Skipping
  the count call left every later call answering `CTL_RESULT_ERROR_NOT_INITIALIZED` on 2026-09-29.
- Use the value type `ctlGetSupported3DCapabilities` reports for a feature, always. Reading a custom-typed feature
  with a scalar type crashed the process inside the driver. Never probe types.
- An enum value is `Value.EnumType.EnableType`, a uint32 at union offset 0. `CTL_RESULT_ERROR_DATA_NOT_FOUND` on a
  get means nothing is stored: publish the default the feature table reports.
- Prefer IGCL over the registry wherever IGCL has the setting, because a registry value only takes hold after a
  restart. The shared GPU memory override is the one registry write. Per-application cleanup deletes only value
  names this plugin recorded appearing under `3DKeys`.
- Offer only values the driver reports. When an enum's supported mask is zero (legacy drivers), offer the header's
  documented members and report a refused write truthfully.

## Writes, readback and lifecycle

- Never gate a write or a control on readback. Write, read back, report `AppliedVerified` on a match and
  `AppliedUnverified` otherwise, and publish the written value as observed. A read used to fill the other fields of
  a structure never decides whether to write.
- A refused write is `Rejected`; a failed one is `Indeterminate`. Neither is retried automatically, and nothing is
  rolled back.
- All IGCL calls run on the plugin's single lane. One IGCL session per cycle: open at start and resume, reopen after
  `CTL_RESULT_ERROR_DEVICE_LOST` or `CTL_RESULT_ERROR_UNINITIALIZED`, close on suspend and stop. Any other failed
  call fails only its control. Stop and dispose stay bounded and idempotent, and never close the session or free
  `ControlLib.dll` under a running driver call.
- Retract descriptors and publish Unavailable health when the library or an Intel adapter is missing. Detection has
  no side effects.
- Observation runs every 10 seconds, republishes a state only when it changed or before WSGM's 30-second freshness
  window expires, and never ends on one failed read. Trace transitions and decisions; polled state goes through
  `TraceChange`. Nothing logs per sample.

## Build and tests

```powershell
dotnet build src/WSGM.Plugin.IntelGpu/WSGM.Plugin.IntelGpu.csproj -c Release
dotnet build tests/WSGM.Plugin.IntelGpu.Tests/WSGM.Plugin.IntelGpu.Tests.csproj -c Release
```

Tests never touch HKLM or a real driver: registry tests use a disposable HKCU subtree through the transports' root
seams, and feature tests build controls without a session. Any claim about driver behaviour needs a run on that
hardware and a dated `PROVENANCE.md` entry.
