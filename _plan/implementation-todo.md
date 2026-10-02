# GPU plugins, Display and Audio

Work continues on `master`. Vendor code belongs to its GPU package; Windows display and
audio mechanisms belong to `windows-device-control`. Hardware validation is separate from blind
implementation.

Current scope: complete the AMD and NVIDIA packages under
[the vendor implementation checklist](gpu-vendor-implementation.md). No test execution or live
driver changes were requested. Unchecked acceptance items remain outstanding; compilation does
not count as a hardware pass.

## Overlay Tools and CEF parity

Full specification: [Overlay Tools parity plan](overlay-tools-cef-parity.md). Feature code is
implemented; live acceptance is pending.

- [x] Audit the four CEF surfaces and their Overlay entry points, services and partial views.
- [x] Enumerate all CEF commands, settings, previews and import workflows in the parity plan.
- [x] Audit other Overlay routes and shared navigation/editor behavior for defects of the same
      caliber.
- [x] Restore all four Tools routes with shared entry/leave lifecycle and native Artwork attachment.
- [x] Correct nested Back precedence, live route visibility, no-op command states, editor focus,
      false empty-library results and missing theme/movie deletion confirmations.
- [x] Add the shared controller file/folder picker, image cache, media viewport and scoped
      view-session ownership.
- [x] Complete CSS Loader Browse, Installed, Profiles and Settings, including screenshots and exact
      patch editors.
- [x] Complete Video Switcher Browse, Library and Settings, including playback and local WebM
      import.
- [x] Complete Artwork game selection, slots, filters, details, local/official/invisible artwork and
      logo editing.
- [x] Complete importer sources/folders, filtered review, launch choices, staged per-title/bulk
      artwork and apply results.
- [x] Reconcile docs/guidance/assets, compile without warnings and review full populated Overlay
      previews.
- [ ] Maintainer manual acceptance for Overlay and CEF parity, then focused tests, baselines and the
      broad implementation gate.

## Overlay and QAM regression fixes, 2026-10-01

- [x] Render GPU controls directly under Device > GPU and remove the Graphics destination.
- [x] Use the Processor cores native Expander style for folding sections.
- [x] Publish GPU categories in QAM Performance through the shared settings renderer.
- [x] Restore Claw chord suppression from before `24368aef` and compare `BlockWinG.zip`.
- [x] Separate Channels and Format in Overlay and QAM, place QAM choices under Audio, and label Spatial Off.
- [x] Resume display-mode reads after regrouping the Overlay Display controls.
- [x] Move Format SD Card from Card Manager to Tools > Storage.
- [x] Remove device/GPU duplicates from Tools > Plugins and hide an empty Plugins entry.
- [x] Compile the Release solution with no warnings, format changed sources, check asset drift/guidance, and review 1280 × 800 and 980 × 640 renders.
- [ ] Maintainer manual pass on Claw and Steam QAM.
- [ ] Run deferred focused tests, review/update affected UI baselines, then run the initial implementation gate.

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
- [x] Finish serialized driver lifecycle, discovery, generations and shutdown.
- [x] Bind documented NVAPI DRS structures, functions and supported setting values.
- [x] Use NoVidiaApp for curated driver controls, including G-SYNC policy, synchronization, power,
      cache and supported DLSS/RTX settings.
- [x] Implement native per-game profiles, owned-setting journals and inherit/reset without erasing unrelated settings.
- [x] Use ColorControl for connected-output color, BPC, dithering and HDR10+ behavior; revalidate
      physical monitor identity and complete color combinations.
- [x] Account for the RTX 5080 and the laptop's Optimus RTX 4070 without assuming NVIDIA drives the panel.
- [x] Add package/build/setup integration, provenance and focused regression sources.
- [x] Compile the Release solution without warnings. Test execution and hardware acceptance remain deferred.

## AMD, issue 179, blind implementation explicitly requested

- [x] Locate HC 1.3.1.6's AMDGPU and ADLX backend.
- [x] Inspect official ADLX/ADL contracts and ColorControl's AMD display path.
- [x] Bind ADLX interfaces from their documented C vtables, with native contract regression sources.
- [x] Discover adapters and their connected displays; never use HC's fixed display index as identity.
- [x] Publish supported FreeSync, scaling, color, dithering and Radeon 3D controls, including
      RSR/AFMF and supported FidelityFX upgrades from the HC/SDK reference.
- [x] Model driver-global, display-wide and host-switched game settings according to actual API scope.
- [x] Investigate native Radeon profile APIs; do not invent native per-application support.
- [x] Implement loss/reconnect, sleep/resume, truthful write outcomes and readback.
- [x] Add package/build/setup integration, focused regression sources and explicit blind provenance.
- [x] Compile the Release solution without warnings. AMD hardware acceptance remains unverified.

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
- [x] Refresh and review the 11 affected headless Overlay baselines for the Intel-branch PR.
- [ ] Commit and push to the Intel branch, publishing changed children first.
- [ ] After the maintainer's manual pass, run focused tests, refresh affected UI baselines and run the required gate.

No new release version, tag or GitHub release is requested. Deployment and live driver actions
still require explicit direction. The PR request invokes the repository's required automated
verification; manual hardware and live Steam acceptance remain pending.

## Overlay and QAM layout follow-up, 2026-10-01

- [x] Stack controller, device and performance folds vertically with consistent native styling.
- [x] Center collapsible title blocks vertically and give summary headers more breathing room.
- [x] Replace section pin labels with centered tack icon buttons and a visible pinned state.
- [x] Compact Overlay Audio into labeled rows without separate cards for Channels, Format or Spatial.
- [x] Group QAM GPU controls into one vendor-named fold with plain inner sections.
- [x] Preserve individual Overlay GPU folds, stacked vertically at full width.
- [x] Complete formatting, asset checks and warning-free compilation.
- [x] Commit and push the toolkit before the WSGM gitlink.
- [x] Rebuild the setup and copy it to Z:.
- [ ] Maintainer manual pass, then focused tests and affected UI baselines.

Handoff: `Z:\WSGM-Setup-2.1.0.exe`, file version `2.1.0.1540`, built from `085987ee`.
The copied setup matches the build output by SHA-256. Release compilation had zero warnings and
errors; isolated Overlay previews were reviewed. Application tests, the full gate and UI baseline
refresh remain deferred until the maintainer reports a manual pass.
