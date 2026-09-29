# Intel Graphics plugin provenance

Every structure layout, value and call sequence here comes from Intel's published `igcl_api.h` and
the samples in `_ref/intel-gcl`, checked against the hardware observations below. Each observation
is evidence from that machine on that day. Nothing Intel ships is redistributed: `ControlLib.dll` is
loaded from the installed driver.

## Laptop probe, 2026-09-29

Intel UHD Graphics, PCI 8086:4688 (Alder Lake), driver 32.0.101.7088, on the maintainer's laptop.
These facts come from a probe run against the driver, not from this plugin, which has not run on
hardware yet.

- `ctlInit` with application version `(1 << 16) | 1` succeeds.
- `ctlEnumerateDevices` must be called with a zero count and a null array first, then with the
  array. Passing the array straight away left every later call answering
  `CTL_RESULT_ERROR_NOT_INITIALIZED` (`0x40000001`).
- `ctl_3d_feature_caps_t` is 24 bytes: `Size` at 0, `Version` at 4, `NumSupportedFeatures` at 8,
  `pFeatureDetails` at 16.
- `ctl_3d_feature_details_t` is 72 bytes: `FeatureType` at 0, `ValueType` at 4, the 24-byte,
  eight-aligned `ctl_property_info_t` at 8, `CustomValueSize` at 32, `pCustomValue` at 40,
  `PerAppSupport` at 48, `ConflictingFeatures` at 56, `FeatureMiscSupport` at 64, then three
  reserved `int16`.
- `ctl_3d_feature_getset_t` is 56 bytes: `Size` at 0, `Version` at 4, `FeatureType` at 8,
  `ApplicationName` at 16, `ApplicationNameLength` at 24, `bSet` at 25, `ValueType` at 28, the
  8-byte `ctl_property_t` at 32, `CustomValueSize` at 40, `pCustomValue` at 48.
- An enum value is `Value.EnumType.EnableType`, a uint32 at union offset 0, as the header and
  Intel's `3D_Feature_Sample_App.cpp` read it.
- Reading a custom-typed feature with a non-custom value type crashed the process with an access
  violation inside the driver. The plugin therefore always uses the value type the feature table
  reports and never probes one.
- A get of a value that was never set answers `CTL_RESULT_ERROR_DATA_NOT_FOUND` (`0x40000014`),
  which means the driver default.
- The feature table listed features 4 (CMAA), 9 (flip modes), 11 (application profiles, custom), 13,
  15 and 17, all with `PerAppSupport` 1. Every enum reported `SupportedTypes` 0. Feature 9 reported
  misc flags `0x16` (DX11, DX12, live change). A set of feature 9 answered
  `CTL_RESULT_ERROR_UNSUPPORTED_FEATURE` on this legacy driver.
- A per-application write, with `ApplicationName` set to the executable's file name without a path,
  appears in the registry as values named `<exe>_<Setting>` under
  `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\NNNN\3DKeys`.
  Observed: `wsgm-probe.exe_Cmaa`, `wsgm-probe.exe_Emul64bitAtomic`,
  `wsgm-probe.exe_FrameGeneration`. Global values are named `Global_<Setting>`, for example
  `Global_AsyncFlipMode`, `Global_EnduranceGaming` and `Global_LowLatency`. IGCL has no delete,
  which is why the plugin records the names a write adds and deletes only those.

## Claw evidence, carried over from the Claw package

Claw 8 AI+ A2VM (`MS-1T52`), the MSI Claw package's reference unit. These are that package's
observations, recorded there first and repeated here because this plugin now owns the paths.

- 2026-08-30, unelevated: Arc Sync reported the panel supported across 30-120 Hz. A write to OFF and
  a restore of the saved profile structure both succeeded and read back. The two-call output
  enumeration (count with a null array, then the array) is required, and every unattached connector
  answers `CTL_RESULT_ERROR_KMD_CALL`: the unit enumerates twelve outputs of which one is real.
  `ctl_init_args_t` is 36 bytes, the monitor parameters 24 and the profile parameters 28.
- 2026-09-10, unelevated: `ctlInit` reported supported version `0x10001`, one adapter enumerated,
  and Endurance Gaming (feature 1, custom, `ctl_endurance_gaming_t` of 8 bytes) read
  `EGControl = OFF`, `EGMode = PERFORMANCE`. Prebuilt shader download (feature 18, bool) answered
  and read enabled. Only the reads are device-verified.
- 2026-09-10: `ctlGetSupported3DCapabilities` returned twelve features. Feature 9 was enum-typed
  with a supported mask of `0x2d`: application default, VSync on, Smooth Sync and capped FPS. The
  element stride derived from the data was 72 bytes, matching the laptop's layout above.
- 2026-09-10: feature 9 read back zero and a write reported success without changing anything, while
  `3DKeys\Global_AsyncFlipMode` did move and held Intel's `ctl_gaming_flip_mode_flag_t` bits (1, 4,
  8 and 32 each wrote and read back, elevated). That probe read the enum as a bool followed by an
  int32 at offset 4, so it read and wrote the wrong bytes; the laptop evidence puts the value at
  offset 0. This plugin writes feature 9 through IGCL at offset 0, **blind until it is exercised on
  the Claw**.
- 2026-09-10, driver 32.0.101.8992: the shared GPU memory override is
  `GMM\GpuSystemMemoryPinninglimit` under the display adapter's class key. At rest it read 57,
  Intel's documented default; `ullTotalPhys` was 33,866,657,792 bytes and the adapter reported
  19,327,352,832, 57.07 % of it. Intel Graphics Software wrote 44 into exactly that value, and its
  reset wrote 57 back rather than deleting it. Nothing else moved. The range 13-87 % is what Intel
  Graphics Software offers. The write is device-verified; the effect after a restart is not. The
  class also held an unreadable `0000` beside the Intel adapter, so each subkey's failure is
  contained.

## Built from the header and samples only

- Scaling, retro scaling, display sharpness, wire format, end-display settings, power savings (PSR,
  FBC, LRR, DPST) and LACE follow `igcl_api.h` and `Samples/Power_Feature_Samples`,
  `Samples/Color_Samples` and `Samples/Scaling_Samples`. Power settings use structure version 1,
  which answers per display output.
- Colour follows `Samples/Color_Samples/Color_Sample_App.cpp`: `CreateOneDLutFromBCG` for
  brightness, contrast and gamma on the last 1D LUT block, and `GenerateHueSaturationMatrix` with
  its BT.709 matrices for hue and saturation on the first 3x3 matrix block, with the sample's
  clipping bounds as the offered ranges.
- Display identity uses `DisplayConfigGetDeviceInfo` with
  `DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME`, keyed by the adapter LUID and the target id IGCL
  reports in `Os_display_encoder_handle`.
