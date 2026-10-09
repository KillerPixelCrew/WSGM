# Device integration rework

Work branch: `chore/device-integration-rework`.

## Accepted direction

- Windows-first .NET LibHandheld supports the complete HC/HHD device target.
- Device facts use HC > HHD > OpenGamepadUI, with pinned references and explicit unresolved variants.
- LibHandheld owns handheld hardware; LibGPUDriverInteract owns Intel/AMD/NVIDIA driver mechanics.
- Generic Windows controls remain in WindowsDeviceControl.
- WSGM consumes the new libraries directly. Retire the Device SDK/device packages and the three
  first-party GPU packages after functionality is preserved.
- Keep the common Plugin SDK for independent integrations such as IR and third-party extensions.
- The two new repositories are private during preparation.

## Preparation and implementation sequence

1. Save the complete [handheld inventory](../external/libhandheld/inventory/README.md) and
   [GPU inventory](../external/libgpu-driver-interact/inventory/README.md) in their own repositories.
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
LibGPUDriverInteract now contains extracted vendor engines and a direct API, with WSGM integration
and migrated regression sources. LibHandheld remains the inventory/planning stage. GPU compilation,
deployment and attended acceptance are recorded with the current delivery; inventory entries alone
do not establish new hardware validation.

## Private library access

The local development checkout includes both private repositories. The maintainer directed that CI
remain inactive for `chore/device-integration-rework` while the new libraries are private and
testing is ongoing. No cross-repository CI key or token was installed. The library build workflow
is manual-only. LibHandheld remains outside the compiled graph; community bundle and VIIPER jobs
retain their public dependency graph. Re-enable automatic application CI after testing and once
the compiled library is accessible to the runner.
