# Device integration rework

Work branch: `chore/device-integration-rework`.

## Accepted direction

- Windows-first .NET LibHandheld supports the complete HC/HHD device target.
- Device facts use HC > HHD > OpenGamepadUI, with pinned references and explicit unresolved
  variants.
- LibHandheld owns handheld hardware; LibGPUDriverInteract owns Intel/AMD/NVIDIA driver mechanics.
- Generic Windows controls remain in WindowsDeviceControl.
- WSGM consumes the new libraries directly. Retire the Device SDK/device packages and the three
  first-party GPU packages after functionality is preserved.
- Keep the common Plugin SDK for independent integrations such as IR and third-party extensions.
- The two new repositories are public; CI remains inactive until explicitly enabled.

## Preparation and implementation sequence

1. Save the complete [handheld inventory](../external/libhandheld/inventory/README.md) and
   [GPU inventory](../external/libgpu-driver-interact/inventory/README.md) in their own
   repositories.
2. Review variant associations, source differences and the Windows transports required by Linux
   mechanisms. Preserve the full device target rather than narrowing it to existing WSGM models.
3. Extract current device and GPU implementations into the corresponding library, retaining
   behavior, native ownership and existing regression cases.
4. Remove WSGM SDK dependencies from reusable hardware code. Account for the common Plugin SDK's
   current Device SDK references; keep library APIs independent from application UI and policy.
5. Integrate the libraries into WSGM, including profiles, AutoTDP, input ownership and both UI
   surfaces. Retire old packaging only after parity, runtime transitions and dependencies work.
6. Validate the real application/device paths and publish the libraries independently.

The library [plans](../external/libhandheld/PLAN.md) and
[GPU extraction plan](../external/libgpu-driver-interact/PLAN.md) hold the detailed boundaries.
LibGPUDriverInteract contains a shared runtime and direct vendor API, with WSGM integration and
migrated regression sources. LibHandheld contains the semantic device API and native family
providers, with 123 runtime definitions including ranges and direct WSGM integration. WSGM retains
profiles, AutoTDP, HidHide, virtual input and OEM policy. Separate Device SDK/device package
projects are removed; common Plugin API 5 retains shared historical namespaces inside its single
assembly and requires plugin rebuilds. Setup detects native definitions and Device Lab produces
library contribution source/fixtures. The full 201 HC/HHD source-record target is unchanged; it is
not a count of independently verified physical devices. Current source coverage and unresolved
decoder evidence remain in the library inventory. The completed integration/review and
build/deployment record is [review delivery](review-since-2.1.0-tasks.md). Inventory entries and
compilation do not establish new hardware validation. PR verification runs under the repository's
explicit PR gate; attended hardware and emulator acceptance remain separate.

## Library access and CI

LibHandheld and LibGPUDriverInteract are public at the maintainer's request. All runners and
contributors can clone the pinned library sources without a private cross-repository credential. The
earlier instruction to keep CI inactive remains in effect until explicitly changed. Both libraries
are in the application graph; the library workflow remains manual-only and WSGM jobs remain gated by
`WSGM_CI_ENABLED`.
