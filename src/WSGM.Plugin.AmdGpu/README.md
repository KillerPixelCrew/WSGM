# WSGM AMD Graphics

An MIT `wsgm.gpu` package for Radeon graphics and display controls. Handheld Companion 1.3.1.6 is
the AMD behavioral reference; ColorControl supplies the display color and dithering reference. The
package binds AMD's documented ADLX/ADL interfaces and loads the installed driver's `amdadlx64.dll`
and optional `atiadlxx.dll`. It ships neither DLL and has no runtime dependency on the reference
apps.

## Controls and scope

Every feature is queried on its actual GPU or connected display. Unsupported interfaces and features
are omitted. Extension interfaces are queried by their documented IID; an older ADLX runtime can
still publish the base features it supports.

| Scope                            | Controls                                                                                                                                                                                                                                                                                                                                                                          |
| -------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| GPU-wide, host-switched per game | Anti-Lag and supported level, Boost and minimum resolution, image sharpening and strength, Enhanced Sync, V-Sync, anti-aliasing mode/level/method, morphological AA, anisotropic filtering/level, tessellation mode/limit, RSR/sharpness, AFMF and supported algorithm/search/performance/fast-motion options, FidelityFX upscaling and frame-generation upgrades/available ratio |
| GPU-wide global-only             | desktop sharpening and explicit shader-cache reset action                                                                                                                                                                                                                                                                                                                         |
| Display-wide global-only         | FreeSync, VSR, GPU scaling, scaling mode, integer scaling, supported output BPC/pixel format, hue/saturation/brightness/contrast/temperature, Vari-Bright enablement/policy, supported FreeSync color accuracy and ADL2 dithering                                                                                                                                                 |

Chill and FRTC are omitted because WSGM/RTSS own frame limiting. Device/OEM TDP, fan and EC controls
remain in the device package. FSR upgrades, RSR and AFMF remain distinct driver features.

ADLX's public 3D setters address a GPU, not an application profile. WSGM's existing `Switched` scope
applies game overrides and transitions/restores them on game changes. The package neither creates a
second foreground-game detector nor invents Radeon profile blobs. The ADL application profile APIs
were investigated, but their public interface does not define a supported setting schema for these
Radeon 3D values. Existing Radeon application profiles remain untouched.

Display settings have the actual display's GPU association. Dithering uses the ADLX-to-ADL mapping
of that display's current adapter/output IDs. It never assumes display index zero. BPC/pixel choices
use the per-value support queries; sliders use the reported minimum, maximum and step. Dithering's
documented ADL mode enum has no per-value mask: a driver refusal is reported rather than treated as
success. HDR is controlled by the shared Windows Display owner; the NVIDIA-specific HDR10+
output-mode extension is not invented for AMD.

## Lifetime and writes

All native calls run on one serialized worker lane. Current target identity, support and range are
revalidated immediately before writing. A failed optional observation does not gate a supported
field write. Successful writes are published as observed, with verified outcomes only when
independent readback matches. Failed or uncertain writes are not automatically retried.

GPU identity comes from PNP. Display identity combines its GPU, ADLX unique ID and EDID. A write
re-enumerates those identities before calling a setter. Observation refreshes topology every ten
seconds, retracts removed descriptors and handles external changes. Ordinary row failures remain
isolated. Driver termination/orphaning invalidates all interface references; the package never calls
Release through those invalid pointers. Suspend/stop release valid feature references before
services/termination and never unload under an active call.

## Validation status

This is a blind implementation requested by the maintainer. No AMD GPU is present in the local
inventory and no hardware scenario or test suite was executed. The curated status remains `blind`.
PROVENANCE.md records sources, ABI details and the hardware acceptance still required by issue #179.
Regression sources cover one-byte booleans, pointer-sized display IDs, reference lifetime,
driver-loss invalidation and signed range/step behavior.
