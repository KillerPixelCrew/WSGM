# WSGM NVIDIA Graphics

An MIT `wsgm.gpu` package for NVIDIA driver settings and NVIDIA-driven outputs. It uses the
installed `nvapi64.dll`; NVIDIA App, NoVidiaApp and ColorControl are reference implementations, not
runtime dependencies. It runs with Device Integration off and alongside other vendors' GPU packages.
The existing host renders it under Device > GPU in Overlay and NVIDIA GPU in QAM.

## Controls

The curated driver catalog follows [NoVidiaApp](https://github.com/KillerPixelCrew/NoVidiaApp).
Discovery queries the installed driver's setting IDs, DWORD types and available values. Public
settings with an empty value table use their documented catalog; private settings require an
enumerated value table. Unsupported settings are omitted. The current effective value is retained in
the choices even when a driver's value table omits its default.

| Family       | Controls                                                                                                                                                                   |
| ------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Performance  | power management, OpenGL threaded optimization, prerender queue, low latency, shader cache, supported Resizable BAR switches                                               |
| Quality      | anisotropic filtering, texture quality/LOD bias, anti-aliasing, gamma correction, FXAA, ambient occlusion, NVIDIA Image Scaling                                            |
| Presentation | V-Sync/Fast Sync, adaptive synchronization, triple buffering, OpenGL/Vulkan presentation, G-SYNC enablement/fullscreen/windowed/requested/application policy and indicator |
| DLSS         | SR/RR/FG overrides, render presets, quality modes and supported multi-frame generation                                                                                     |
| Other        | supported Smooth Motion, RTX HDR, RTX Digital Vibrance and VR queue settings                                                                                               |

Driver frame limiters are omitted because WSGM/RTSS already own the frame limit. No setting is
written merely to discover support. Private DWORD settings are isolated in the catalog and gated by
the driver's enumeration; their IDs come from the maintainer's NoVidiaApp reference.

Each active NVIDIA output has its own section. ColorControl is the reference for output bit depth,
RGB/YCC encoding, full/limited range, colorimetry, desktop depth, color selection, dithering and HDR
output mode. Complete color combinations are checked with `NV_COLOR_CMD_IS_SUPPORTED_COLOR` before
publication and again before writing. A format change lets the driver choose compatible colorimetry;
other unedited color fields are preserved.

Dithering offers Auto/Enabled/Disabled and the reported depth/pattern masks. Depth and pattern are
shown while enabled, since the driver ignores them in Auto. These are immediate driver calls; there
are no registry writes or automatic driver restarts.

HDR output mode offers SDR, HDR10 and HDR10+ Gaming when the output reports Gaming support. HDR10+
video support alone does not qualify. The shared Windows HDR switch remains in the Windows Display
surface. Returning from HDR10+ to HDR10 uses ColorControl's off/on workaround only for the exact
captured Windows target whose HDR state was On, restoring that state even after an inconclusive Off
readback. HDR10+ and dithering APIs are optional private entry points. See PROVENANCE.md.

## Profiles and ownership

DRS is authoritative. Shader cache and global G-SYNC policies are global-only; normal gaming driver
settings use native per-application profiles. WSGM supplies its existing game identities and plain
executable filenames. No foreground-game detection or second profile store is added.

The package finds the executable's existing native profile, or creates a stable WSGM profile for an
unassociated executable. It changes only requested settings. Its atomic private journal records
prior explicit values, original inheritance and write intent before SaveSettings. Reset/inherit
restores the prior explicit value or deletes only the user override WSGM added. NVIDIA's predefined
application defaults and unrelated settings remain intact. Empty native profile containers are
retained rather than risking deletion of externally added content.

External edits are preserved during cleanup. An unchanged desired value is not repeatedly forced
over an external edit. An unconfirmed save or restoration is read back on a later pass but never
blindly repeated. Conflicting WSGM games sharing a native profile/setting are refused; identical
requests are coalesced. Unreadable ownership state aborts instead of replacing it with an empty
journal.

## Lifetime and display identity

Before publishing controls, discovery reads all writable settings and probes their setters with
unchanged native state. Shared color/dithering structures are checked once, preserving color policy,
disabled state, bit depth and mode; HDR output-mode probes do not invoke transition workarounds. For
DRS, identical explicit values are saved, while inherited/default values are validated in the
session and discarded with LoadSettings so no persistent override is created. Read-only rows and
action buttons are never written. Failed or uncertain probes are cached without retries on polling
or reconnect. Later user write failures do not remove controls from the current layout.

All driver operations use one serialized worker lane. Deadline/cancellation admission is checked
again immediately before setters after preparatory reads. Observation runs every ten seconds and
refreshes state before the host's freshness interval expires. Ordinary row failures stay isolated;
invalidated/uninitialized driver handles retract the descriptor set and reopen later. Suspend and
stop close admission before releasing resources, and never unload a DLL under a driver call.

Outputs come from physical GPU connected-display enumeration, not from GPU presence alone. The
driver maps GDI source names to display IDs to attach Windows monitor identities. Each output is
revalidated before writes, including its current physical GPU handle and captured monitor identity.
Where available, the Windows monitor path supplies the persisted instance key and friendly name. A
headless Optimus GPU still offers driver settings but no invented display controls.

## Validation status

Implementation and compilation are separate from driver acceptance. No live driver read/write
scenario or GPU test suite was requested or run for this implementation. The package remains curated
as `blind`. PROVENANCE.md records references and the local GPU inventory without claiming a hardware
pass. Regression sources cover ownership, partial saves, inheritance, external edits, shared
profiles, color field preservation and action admission.
