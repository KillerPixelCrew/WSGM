# GPU plugins, Display and Audio

Work continues on `intel-gpu-plugin`. Vendor code belongs to its GPU package; Windows display and
audio mechanisms belong to `windows-device-control`. Hardware validation is separate from blind
implementation.

Order: finish the shared GPU runtime and NVIDIA package, implement the AMD package from official
SDKs and HC, complete Windows Display/HDR and Audio controls, then compile and deliver for manual
validation. Unchecked items are outstanding; compilation does not count as a hardware pass.

## Original issues 201 and 202

- [x] Implement the reusable folding Overlay section and documented session expansion state.
- [x] Implement sound-pack management, preview, reversible Steam overrides and restore defaults.
- [x] Merge these implementations into the Intel branch.
- [ ] Verify issue 201 in the running Overlay: controller, mouse/touch, dynamic sections and focus restoration.
- [ ] Verify issue 202 on Windows Steam Stable/Beta: pack switching, preview, restore, restart/reload and failure recovery.
- [ ] Run the deferred focused tests and gate after the maintainer's manual pass.
- [ ] Reconcile remaining acceptance gaps before considering either issue finished.

## Integration

- [x] Merge sound packs and folding Overlay groups into the Intel branch.
- [x] Merge and publish the toolkit child before the parent gitlink.
- [x] Keep one typed Graphics capability/profile backend, reachable from Device > GPU.
- [x] Compile the merged solution without warnings.

## Intel, issue 178

- [x] Preserve the standalone Intel plugin and typed GPU capability/profile surfaces during the merge.
- [x] Keep Device > GPU connected to the shared Graphics controls.
- [ ] Audit the final extraction against issues 32, 33 and 37, including configuration migration and retained Claw controls.
- [ ] Verify plugin loading, Device Integration off, absence of duplicate controls, sleep/resume and driver reconnect.
- [ ] Record which Intel hardware scenarios were actually tested and which remain unavailable.

## NVIDIA, issue 177

- [x] Inspect official NVAPI headers and Driver Settings documentation.
- [x] Clone ColorControl and identify its NVIDIA display-color implementation.
- [ ] Finish serialized driver lifecycle, discovery, generations and shutdown.
- [ ] Bind documented NVAPI DRS structures, functions and supported setting values.
- [ ] Add curated driver controls, including G-SYNC policy, synchronization, power and cache settings.
- [ ] Implement native per-game profiles, owned-setting journals and inherit/reset without erasing unrelated settings.
- [ ] Enumerate NVIDIA-connected outputs, revalidate output identity and publish supported color controls.
- [ ] Account for the RTX 5080 and the laptop's Optimus RTX 4070 without assuming NVIDIA drives the panel.
- [ ] Add package/build/setup integration, provenance and focused regression coverage.
- [ ] Compile, then hand over for manual testing on the available NVIDIA hardware.

## AMD, issue 179, blind implementation explicitly requested

- [x] Locate HC 1.3.1.6's AMDGPU and ADLX backend.
- [x] Inspect official ADLX/ADL contracts and ColorControl's AMD display path.
- [ ] Bind ADLX interfaces from their documented C vtables, with exact ABI coverage.
- [ ] Discover adapters and their connected displays; never use HC's fixed display index as identity.
- [ ] Publish supported FreeSync, scaling, color and Radeon 3D controls.
- [ ] Model driver-global, display-wide and host-switched game settings according to actual API scope.
- [ ] Investigate native Radeon profile APIs; do not invent native per-application support.
- [ ] Implement loss/reconnect, sleep/resume, truthful write outcomes and readback.
- [ ] Add package/build/setup integration, focused coverage and explicit blind provenance.
- [ ] Compile. AMD hardware validation remains unavailable; no AMD GPU is currently owned.

## Windows Display

- [ ] Add a dedicated Display section under Device, available with Device Integration off.
- [ ] Reuse the current Windows brightness/display owners and retain their pin identities.
- [ ] Keep HDR capability, get/set and topology handling in `windows-device-control`.
- [ ] Expose HDR from the Windows Display surface, separate from vendor color controls.
- [ ] Account for every control moved out of Tools > Display and preserve its navigation path.

## Overlay Audio

- [ ] Verify and complete the existing spatial-audio controls and service attachment.
- [ ] Add a dedicated Channels selector, separate from Format.
- [ ] Restrict Format to supported sample-rate/bit-depth combinations for the chosen channels.
- [ ] Preserve endpoint identity, channel mask and other fields when either selector changes.
- [ ] Keep failed/uncertain writes visible, with no automatic retry.
- [ ] Add focused coverage for independent selection, endpoint changes and unsupported combinations.

## Delivery and validation

- [ ] Update mechanism docs, package provenance and this checklist as work lands.
- [ ] Regenerate Steam assets after toolkit/source changes and check drift.
- [ ] Run formatting, guidance checks and warning-free Release compilation.
- [ ] Commit and push to the Intel branch, publishing changed children first.
- [ ] After the maintainer's manual pass, run focused tests, refresh affected UI baselines and run the required gate.

No new release version, tag or GitHub release is requested. Deployment and live driver actions
still require explicit direction. Test execution remains deferred under the manual-first guide.
