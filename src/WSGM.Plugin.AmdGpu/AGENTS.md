# AMD Graphics package

This MIT common `wsgm.gpu` package owns Radeon driver behavior. Handheld Companion 1.3.1.6 is the
behavioral reference for AMD graphics controls, especially RSR, AFMF and scaling. ColorControl is
the display bit depth, color and dithering reference. Official AMD ADLX/ADL contracts decide the
binding ABI and enums. Read README.md and PROVENANCE.md before changing these paths.

- The installed driver supplies `amdadlx64.dll` and `atiadlxx.dll`. Do not redistribute driver DLLs
  or depend on either reference app at runtime.
- `IADLXSystem` and `IADLMapping` have no Acquire/Release prefix. Other interfaces do. `adlx_bool`
  is one byte, `adlx_int` is four, `adlx_size` is pointer-sized. Each C vtable slot comes from the
  pinned header, including extension interfaces queried by their documented IID.
- ADLX's 3D APIs are GPU-wide. WSGM's existing Switched profile scope owns game activation/exit;
  display settings are GlobalOnly. Do not invent native Radeon profile encodings.
- Discover support per GPU/output, keep reference-counted objects alive while controls use them,
  and release features before services and ADLXTerminate. All calls share the native lane.
- Before publishing controls, read all settings and round-trip current native values through their setters.
  Failed startup checks omit controls; cache failures without retrying on polling/reconnect. Do not probe actions
  or remove a published control after a later user write fails. Preserve disabled state and native values.
- Validate the target's PNP identity and the display's unique ID, EDID and attached GPU before a
  write. ADL dithering uses ADLX's mapping, never a fixed adapter or display index.
- Readback cannot gate a supported field write. Publish successful written values as observed,
  verify matches when possible, and report uncertain outcomes without automatically retrying.
- Observation failures stay isolated to their control unless ADLX is terminated/orphaned. Driver
  loss retracts descriptors and reopens on a later observation. Never unload under an active call.
- Follow the root manual-first validation policy. Hardware behavior remains unverified until the
  maintainer directs and performs that scenario. Compilation is not hardware evidence.
