# AMD source and validation record

Implementation date: 2026-10-02. Issue [#179](https://github.com/KillerPixelCrew/WSGM/issues/179).

The maintainer requested full implementation, with Handheld Companion as the AMD reference and
ColorControl for display bit depth, color and dithering. The issue's hardware acceptance remains
separate from this code delivery.

| Reference                                                                                                                                            | Use                                                                                                                                                                      |
| ---------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `_ref/HandheldCompanion`, decompiled 1.3.1.6                                                                                                         | AMDGPU.cs and ADLXBackend.cs behavior for RSR, AFMF 2.1 options, scaling and support/state/set flows                                                                     |
| [AMD ADLX, 32b5a740d42295c5dfe9026b9f52683da0f3af91](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/tree/32b5a740d42295c5dfe9026b9f52683da0f3af91) | official 2.0.0.125 C contracts and extension headers: ISystem, I3DSettings through Services3, IDisplays through Services3, IDisplaySettings, ICollections and ADLX types |
| [AMD ADL display APIs](https://gpuopen-librariesandsdks.github.io/adl/group__DISPLAYAPI.html)                                                        | documented ADL2_Display_DitherState_Get/Set                                                                                                                              |
| [ColorControl, d0d3bb4682c483b0b2c14a794958abf2d142100e](https://github.com/Maassoft/ColorControl/tree/d0d3bb4682c483b0b2c14a794958abf2d142100e)     | bit-depth/pixel mappings, ADL2 context and dithering enum/behavior                                                                                                       |

No reference source or vendor binary is a build input or runtime dependency. HC's wrapper DLL and
fixed display index are not copied. Its behavior is reimplemented through the documented interfaces
with enumeration and revalidation of every target.

## ABI

ADLX exports are Cdecl; C interface methods are Stdcall (the standard Windows x64 ABI). Booleans are
one byte, integers/enums are four bytes, sizes and pointers are eight bytes in the x64 package.
`ADLX_IntRange` is twelve bytes, with min/max/step at offsets 0/4/8.

`IADLXSystem` starts directly with GetHybridGraphicsType/GetGPUs/QueryInterface/GetDisplaysServices;
it has no reference-count prefix. Get3DSettingsServices is slot 7. `IADLMapping` has no prefix;
ADLIdsFromADLXDisplay is slot 5. Other interfaces have Acquire/Release/QueryInterface at 0/1/2.
Typed lists use Size 3, Begin 5 and At 11. GPU PNPString is 9; display EDID/GetGPU/UniqueId are
7/12/13. Every acquired feature/list item/service is released while still valid.

The base 3D service selectors are Anti-Lag 3, Boost 5, sharpening 6, Enhanced Sync 7, V-Sync 8, AA
10, morphological AA 11, anisotropic 12, tessellation 13, RSR 14 and reset-cache 15. Service1 adds
AFMF 17, Service2 desktop sharpening 18 and Service3 FidelityFX upgrades 19/20. RSR and AFMF getters
take only the service and an output-interface address. They do not take a GPU argument; the other
per-GPU getters retain their target argument. AFMF1's IsSupportedAlgorithm at slot 6 precedes
GetAlgorithm 7; omitting that method shifts every later entry and is unsafe. Frame-gen upgrade's
IsSupported is slot 5, not 3; its available ratio list comes from slot 3.

Display service selectors are FreeSync 9, VSR 10, GPU scaling 11, scaling mode 12, integer scaling
13, BPC 14, pixel format 15, custom color 16 and Vari-Bright 19. Service3 adds FreeSync color
accuracy at 23. BPC and pixel format use their IsSupportedValue method at 6. Dithering uses the
current adapter/output returned by IADLMapping, with a separate owned ADL2 context.

The initialization request is bounded by the SDK header version and installed runtime version.
Missing extension interfaces simply omit their features. A preexisting ADLX initialization is not
taken over or terminated. Terminated/orphaned results invalidate an epoch shared by all held
interfaces before stack unwinding, so disposal cannot call an invalid vtable.

## Acceptance

No AMD hardware, driver write, live readback scenario or GPU test suite was exercised. No exact AMD
driver/display topology is therefore recorded as validated. Current source/build/package evidence
does not satisfy #179's real-hardware acceptance.

Remaining acceptance: AMD APU and discrete adapter enumeration, native support/readback, global
changes and host-switched game transitions/inherit, external Radeon edits, FreeSync and non-FreeSync
outputs, bit depth/pixel format/dithering, external display/hybrid/mux changes, sleep/resume and
driver restart/update. Record exact GPU/APU, driver and display topology when a scenario is
directed.
