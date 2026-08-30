# WSGM 2.0 simplification and delivery plan

Status: structural simplification complete in source and build (2026-08-29); the Steam UI revival
push (S12/S14/S15) landed 2026-08-30 and is live on the reference device; what remains below is
attended acceptance, a handful of named feature gaps, and the Q16 CEF unification.

Branch: `2.0` (PR #19 → master)

Purpose: simplify the implementation without removing any fixed 2.0 feature.

This is the only progress tracker. The other `_plan` files describe architecture, decisions,
requirements, exact Claw hardware facts, and glyph behavior; they are not parallel checklists.
Completed phases below are compressed to the decisions and constraints that must not be
re-litigated; the full narratives live in git history at the commits that closed them.

## Non-negotiable outcome

The overhaul preserves: a real public Device SDK and community Device Plugins; full MSI Claw 8 AI+
A2VM support; Device Lab GUI and CLI workflows; Steam Deck Composite, Xbox 360, and DualShock 4;
global and per-application controller targets; HidHide, managed WSGM UI input, output routing, and
Steam Input fallback; persistent Steam CEF, native QAM, RTSS, and shared performance state;
frametime-driven AutoTDP; plugin-owned physical glyphs in Steam and WSGM; the Home/Steam/Device/
System overlay redesign; and safe update, uninstall, handoff, recovery, and release validation.

No simplification item may satisfy itself by deleting, disabling, or indefinitely deferring one of
these outcomes.

## The architecture that replaced the framework

Roughly 68,000 lines of hypothetical multi-plugin ecosystem (ranking, trust tiers, evidence locks,
promotion, traceability generation, and the projects that hosted it) were replaced by:

```text
WSGM.exe
  ├─ DeviceRuntime ── DeviceHost.exe ── one installed plugin ── hardware
  ├─ ControllerManager ── VIIPER + HidHide ── Deck / X360 / DS4
  ├─ PerformanceManager ── RTSS + AutoTDP
  ├─ SteamUiHost ── native QAM + physical glyphs
  └─ Overlay and Settings consume those same services

WSGM.Device.Sdk
  one AOT-safe public semantic API shared by WSGM, DeviceHost, plugins, and Device Lab
```

**The hard one-plugin rule is fully implemented and tested**: package-root cardinality is counted
before anything else starts; 0 → core WSGM with Device Integration unavailable; 1 → validate or
show the device error; 2+ → refuse normal startup listing each path, with recovery/maintenance
commands bypassing the refusal. Updates stage outside discovery and atomically replace the sole
slot. Project count is not a score: a project remains separate only for NativeAOT, executable
lifetime, packaging, or public plugin ownership.

## Completed phases — decisions that stand

Every item in S0–S5, S7 (source), S9 (source), S10 (source), S11 (source), and S13 (source) is
done with tests, diagnostics, and docs; `eng/verify.ps1` and `build.ps1` pass. What follows is the
distilled record; do not re-litigate these.

- **S0/S0.1 Explorer semantics.** The normal desktop handoff produces an initialized,
  medium-integrity, current-session, **jobless** Explorer via a captured canonical shell parent
  (`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`); scheduled-task Explorer launch is recovery-only and
  reported degraded. Detail in `docs/boot-and-shell.md` / `docs/elevation.md`. Measured 2026-08-29:
  a dead designated parent still parents a child (attribute half proven); whether it still supplies
  token/job association is the narrower open question below.
- **S1 Governance removal.** Traceability manifests, evidence locks, requirement IDs: gone. One
  architecture doc, one decision record, one requirements doc, this tracker.
- **S2 Protected slot.** Trust tiers, ranking, signature rotation, quarantine: gone. Path
  containment, protected-directory enforcement, package-local loading, and the install warning stay
  because they prevent concrete broken/elevation paths.
- **S3 Thin SDK.** One integer API version, one `IDevicePlugin` lifecycle, exact wire equality (no
  min/max negotiation), adopt-or-delete applied to every test-only contract type, one glyph loader,
  one JSON context per device assembly. AOT-clean is proven by `eng/check-aot-isolation.ps1`.
- **S4 DeviceHost.** One package load, one lifecycle, one pipe, one shared input ring (42 ns/sample),
  one cycle generation, one restart policy, bounded shutdown. No generic idempotency caches — an
  uncertain hardware write returns its uncertainty, never auto-retries.
- **S5 Device Lab.** One app, GUI+CLI over the same operations; fixed CLI verb set mirrored in the
  root `AGENTS.md` safe-command table; one capture schema; attended-only hardware mutation with no
  `--yes` by design.
- **S6 Claw plugin (source).** Direct services, one command gate validating identity/ownership/
  range/state immediately before each write, one plugin-owned recovery record. Twelve capabilities
  published. Remaining hardware gaps are the open items below.
- **S7 Controller management (source).** Backend is **VIIPER** (decision + evidence in
  `third_party/controller/README.md`): native full Neptune frame incl. rear paddles and stick
  touch, rides usbip-win2's signed driver (pinned 0.9.7.7 — 0.9.7.8 has an open every-attach BSOD
  on this Windows build), idle cost measured 0.82% of the machine. Attach verified against the real
  driver incl. the 1100/1116-byte `plugin_hardware` dual-layout patch. One `ControllerManager` owns
  targets, HidHide owned-delta, UI capture, zero-output, the unified make-safe sequence, and the
  truthful SDL fallback; `UiInputRouter` swaps navigation onto the managed stream only after the
  first delivered sample, with held-control suppression and timeout.
- **S8 Steam UI / RTSS / AutoTDP (source).** One persistent transport, one bootstrap, per-patch
  independence with native Valve fallback; deterministic TypeScript asset build with drift/hash
  gate; `RtssFrametimeReader` layout device-verified as an executable specification; one pure
  AutoTDP controller with trace replay. The seven-fault "make the QAM reach the user" and
  seven-fault glyph-delivery battles are recorded in git and `docs/steam-cef.md`; their standing
  rules: every label through `localizeOr` (Steam's localizer returns the token for a miss), asset
  hash is part of bridge identity, allowlist entry per patch id, send-side size caps on WSGM's own
  expressions are forbidden.
- **S8 pseudo-security sweep — complete, nine sites.** The test: name the attacker and what they
  cannot already do through an open door beside the check. Removed: the glyph SVG sanitizer's
  allowlist (a plugin already holds WMI/HID/EC). Kept as real boundaries: the `SteamUiBridge`
  command allowlist (attacker: any other CEF injector) and the splash-theme extraction defence set
  (attacker: a theme author with no other access). Kept as correctness, not security: diskpart
  label sanitization, transport target allowlists, HidHide allowlist, UiCapture host allowlist.
- **S9 Glyphs (source).** Physical glyphs are **CSS** (CSSLoader's mechanism), one WSGM-owned
  stylesheet; WSGM owns method (selectors, injection), plugin owns artwork (data URIs). **SVG stays,
  PNG rejected** — glyphs render at many sizes. Absence is the default: a plugin declares what its
  device HAS. Binding sub-rows hide via the one build-independent hook
  (`[id*="EControllerModeInput"]:has(img[src=…])`). OEM buttons map to Guide/QuickAccess in the
  plugin's sample stream; WSGM claims neither hardware button by default.
- **S10 Overlay (source).** Four destinations, bounded page stack, one Back decision, focus/scroll
  memory; every action calls its owning service; eight Device sections as pages; AutoTDP, profile
  selection, controller target, cycle recovery, and glyph preview/input-test are direct rows — the
  pseudo-capability dispatch pattern is dead. Settings reaches stored configuration only.
- **S11 Build/installer/shutdown (source).** One shutdown coordinator with one outer deadline; one
  installer completion channel; usbip-win2 ships as an explicitly ticked, re-verified, non-fatal
  setup task (never from the running shell — INV-020); controller pin asserted by
  `eng/assert-controller-pin.ps1`.
- **S13 Plugin settings (source).** Setting-vs-capability boundary is D22b. Text kind with
  `PlainText` treatment, sections with bounded custom titles, device+plugin-keyed storage with
  load-time revalidation, wire delivery via the closed message enum, the reusable curve editor
  whose every gesture yields a router-valid curve, fan/RGB profile authoring with reference-based
  per-app selection. The synthetic Device Lab plugin exercises the page without hardware.
- **PR #19 review round 1 (Codex, commits `75494c6`…`e7386e2`): all 43 verified findings fixed**;
  five more checked and not carried (recorded 2026-08-29; the not-carried five: asset hash was
  already current, `UiSampleReceived` has its subscriber, glyph identity is supplied, HidHide
  cycle-start half fixed, `PhysicalGlyphService` switch is presentation not policy). Three fixes
  touch device-only paths and re-verify at release: persistent-lighting rollback, gyroscope
  staleness bound, patch remove-after-failed-verify. A second, larger review round (122 comments +
  full 520-file fresh review) is running 2026-08-30; its verified results land in a new section
  here when complete.
- **S12/S14/S15 Steam UI revival (source + live, 2026-08-30).** The gate taxonomy governs all of
  it: supply an absent JS namespace (Perf, Audio), overlay one RPC answer (SteamOS Manager
  `GetState` for TDP), override one store getter/flag (network availability, brightness), replace
  a stub service's methods + invalidate its react-query (Bluetooth), mount Valve components by
  localization token, watch client settings (`steamos_tdp_limit*`), feed a store's own ingestion
  path — never the global `TS.IS_STEAMOS` (D16). Standing rules paid for as production bugs, all
  recorded in `docs/steam-cef.md`: ownership markers on everything injected (the
  self-incompatibility teardown trap struck three times), the second-gate rule, protobuf base64 at
  namespace boundaries (`UpdateSettings`/`SetSetting`), argument order read from client source
  (audio direction enum Input=0/Output=1, 3-ary `SetDeviceVolume`), `actionGeneration > 0`, every
  refused request logged host-side, observable feeds write only on their own change.
  Live on the device: Performance tab (Valve profile header, per-game toggle, reset, overlay-level
  selector; WSGM's unified SteamOS-style frame-limit row — notchless cap, "60 FPS (60 Hz)" pairing
  label, disable switch that turns the slider into a mode-notched refresh-rate control; VRR as
  WSGM's row because Valve's is gated on the absent `SteamClient.System.DisplayManager`; Valve's
  TDP toggle+slider over the RPC seam, verified to the EC at 28 W), Quick Settings (resolution,
  refresh rate), Wi-Fi page with real network enumeration, Bluetooth page, audio devices+volume,
  and brightness — where the founding assumption was device-disproved: Steam's `SetBrightness` is
  a stub on Windows, so WSGM is the backend via `\\.\LCD` (`Interop/NativeBacklight.cs`, flat
  Win32, no COM) with a 2 s external-change poll. Frame-limit strategy is a Settings choice
  (`FrameLimitOnly` default / `NativeModes` / `FrameDoubling`).

## Open items

### Hardware and platform gaps

- [ ] **S0.1 — dead-parent token/job proof.** Whether a designated parent that has exited still
      supplies the medium token and job association (not merely the recorded parent pid). Needs an
      elevated run with a parent at a different integrity level. The live anchor stays the normal
      path until answered.
- [ ] **S6 — Claw charge limit and RGB effects.** `ChargeLimitAddress = 0xD7` is declared and
      unused; the byte's encoding must be read, never guessed (MSI commonly packs an enable bit
      with the percentage). Lighting exposes brightness and zone colour but no effect/animation.
      The old blocker is gone: the elevated `MSI_ACPI`/`Get_Data` read path was proven working on
      2026-08-30 (0x50/0x51/0xD2 read via `Package_32` embedded instance — see the scratch script
      pattern in git); run it for 0xD4/0xD7/0x98 with the device idle. Writes stay attended.
- [ ] **S6 — integration-off external-manager observation.** Attended: another manager driving the
      Claw with WSGM installed and integration off, and nothing of WSGM's moving. The decidable
      half (master switch beats child preferences; make-safe ordering both directions) is pinned by
      `DeviceIntegrationOffTests`.
- [ ] **S8 — replace hand-rolled RTSS interop with `RTSSSharedMemoryNET`** (HandheldCompanion's
      library), vendored as a pinned `third_party/` source build. Replaces `RtssFrametimeReader` and
      `RtssProfileApi`; keeps `RtssDiscovery` (WSGM's own trust question) and the
      `IFrametimeSource`/`IRtssAdapter` seams. Prove NativeAOT by publishing, not reading.
- [ ] **S12 — retire the duplicated performance rows after soak.** Valve's TDP pair now mounts over
      the RPC seam, but the hand-rolled TDP row is still registered and mounted beside it, and the
      retired-but-present `valveFrameLimit`/`valveVrr` mount code and patch classes remain as
      rollback. One release of soak, then delete the duplicates (tracked in Q16 phase 4 too).
- [ ] **S12 — charge-limit fields on the Manager seam.** The gate overlays only the TDP fields;
      `is_charge_limit_available` / `charge_limit_min/max/default` ride the same `GetState` once
      the S6 encoding read lands.
- [ ] **S12 — project the same performance services onto the overlay**, which stays the complete
      surface (unified frame-limit/refresh semantics and strategy awareness reached the QAM first).
- [ ] **S14 — night mode as a WSGM-owned control** backed by Windows Night Light. Valve's row
      cannot be revived: its support hook is `TS.IN_GAMESCOPE` behind a non-configurable export
      descriptor, so the only route is the global constant D16 forbids. Re-scope before building
      (shape: like TDP/AutoTDP rows, not a revival).
- [ ] **S15 — microphone volume backend.** The mic slider is honestly grey: WSGM observes only the
      default output endpoint. Per-direction volume needs capture-endpoint support in
      `native\VolumeControl` plus an input volume in the published audio state.
- [ ] **S15 — WASAPI per-session enumeration and per-app volume** in `native\VolumeControl`,
      exposed through `AudioManager`; serves the custom taskbar and Steam's mixer from one backend.
      Until then `GetApps` correctly answers empty.
- [ ] **S15 — speaker configuration.** Two unknowns need multichannel hardware first: whether
      5.1/7.1 can be read/written at all (the Claw exposes one stereo endpoint), and whether HDMI
      endpoint identity survives a display change. Then implement through
      `IPolicyConfig::SetDeviceFormat` (already declared and used for `SetDefaultEndpoint`; never
      write `PKEY_AudioEndpoint_PhysicalSpeakers`/`PKEY_AudioEngine_DeviceFormat` via
      `IPropertyStore`), and replace the `CAudio_SetSpeakerConfiguration` /
      `PlaySpeakerTestOnChannel` stubs so Steam's dropdown and per-channel test drive it. The point
      is persistence: Windows loses the configuration across display changes; WSGM reapplies per
      endpoint like display profiles.

### Attended acceptance (release evidence; never unattended)

- [ ] **S0.1** — affected-device shell matrix: splash cancellation before/after Explorer exit,
      repeated transitions, crash/restore, taskbar/tray/UWP/touch, MO2 launching a game without
      error 5, no Job tab on the restored Explorer; install over an older job-bound desktop, prove
      refusal, then one sign-out and a clean cycle.
- [ ] **S7** — targets, per-app selection, slots, duplicate input, suspend/resume, host fault,
      external owner, on the reference unit.
- [ ] **S8** — live Steam context churn, focus/navigation, RTSS external edits/restart, AutoTDP
      games/menus/scenes/suspend/manual override, performance, cleanup. (Frametime read from a
      rendering game still outstanding; AutoTDP loop writes power and stays attended.)
- [x] **S9** — visual acceptance of the A2VM profile, OEM sides, M1-left/M2-right orientation at
      supported scales. Accepted by the maintainer on the reference device, 2026-08-30.
- [ ] **S10** — controller, touch, keyboard, scaling, accessibility, themes, cancellation,
      disposal, responsiveness on the handheld.
- [ ] **S11** — atomic one-plugin update, rollback by reinstall, uninstall, external-state
      preservation, recovery-first bypass; then the focused suite, NativeAOT build, live Steam
      matrix, attended Claw/controller/AutoTDP/glyph/transition tests, soak/performance.
- [ ] **S12** — live matrix with the panel mounted: per-game profile switching against a real
      game, each frame-limit strategy incl. an exclusive-fullscreen title across a mode change,
      VRR toggling with a rendering game, recovery when Steam restarts under the shim.
- [ ] **S13** — the page rendered from the Claw plugin's real manifest, gamepad+touch navigation,
      a curve authored in Settings then applied from the overlay, behaviour after a plugin update
      narrows a range a stored value no longer satisfies. (The Settings save-path for plugin
      edits — applied to the freshly-read config, not the page's copy — re-verify here.)
- [ ] **S14** — Wi-Fi enumerate/connect/forget/airplane, Bluetooth pair→forget incl. a controller,
      audio device switching while a game runs, brightness across lock/resume, night mode (once
      built), a resolution change with a game running, and recovery of every surface when Steam
      restarts underneath the patches.
- [ ] **S15** — device switching while a game runs, volume buttons, per-app volume against a real
      mixer; once multichannel hardware exists: 5.1/7.1 selection, per-channel test, display change
      with configuration restored.

## Immediate queue

Only this list drives the next implementation work:

- [x] **Q01–Q07** — checkpoint stabilization, Explorer semantics, governance removal, one-plugin
      invariant, SDK collapse, DeviceHost/Lab merge, Claw plugin simplification. All source-complete;
      their attended gates live above.
- [ ] **Q08 — controller management.** Source-complete on VIIPER end to end (see S7). Remains:
      the attended S7 acceptance matrix, plus the two additional target encoders (X360, DS4) that
      `ViiperControllerBackend.Supported` does not yet offer — advertised targets are gated on the
      backend's real capability until they exist.
- [ ] **Q09 — Steam/QAM/RTSS/AutoTDP.** Source-complete. Remains: the attended S8 matrix and the
      `RTSSSharedMemoryNET` swap above.
- [x] **Q10 — glyph delivery.** Source-complete, live-verified, and visually accepted on the
      reference device 2026-08-30.
- [ ] **Q11 — overlay, shutdown/installer, release validation.** Source-complete. Remains: S10/S11
      attended validation.
- [ ] **Q12 — per-application performance via Steam's own UI.** **Implemented 2026-08-30**, live on
      the device: `SteamClient.System.Perf` supplied with protobuf-named state; Valve's profile
      header/per-game toggle/reset/overlay-level mounted by token; the SteamOS Manager RPC seam
      feeds Valve's own TDP toggle+slider (writes verified to the EC); WSGM's unified frame-limit
      row replaces Valve's notch slider deliberately (SteamOS itself unified the two controls; the
      free cap + snapping pairing + refresh-mode switch is the SteamOS behaviour); VRR is WSGM's
      row because Valve's is gated on the absent `DisplayManager` namespace. Remains: the S12
      attended matrix, the row-retirement soak, the charge-limit seam fields, and — as its own
      future seam — supplying `SteamClient.System.DisplayManager`, which would restore Valve's VRR
      row and retire the `_external` field twins at once.
- [ ] **Q13 — plugin-declared settings page and profile authoring.** Source-complete through S13
      (manifest, sections, storage, wire delivery, rendered page, curve editor, fan/RGB authoring,
      per-app profile selection). Remains: the S13 attended validation, including the save-path
      re-verify.
- [ ] **Q14 — Quick Settings, Internet, Bluetooth.** **Implemented 2026-08-30** except night mode:
      network gate + real enumeration driven by Steam's own scan calls (the silent killer was
      `actionGeneration: 0` rejection — every gate request was dropped by WSGM's own bridge),
      Bluetooth service over the radio backend, brightness with WSGM as the backend, resolution and
      refresh in Quick Settings. Remains: night mode (WSGM-owned, above) and the S14 attended
      matrix.
- [x] **Q15 — Steam audio settings.** **Working live and accepted by the maintainer 2026-08-30**:
      namespace supplied, devices and default switching, output volume slider and hardware-button
      tracking — after the direction enum (Input=0/Output=1) and 3-ary `SetDeviceVolume` were read
      out of the client. The one accepted gap is the microphone volume slider, tracked as its own
      open item above; per-app volume and speaker configuration stay open as S15 platform gaps
      (the latter blocked on multichannel hardware either way).
- [ ] **Q16 — collapse the two CEF generations into one system.** Twelve generation-1 modules
      (badge, library tabs, download sort, network indicator, artwork, launch config, downloads,
      library folders/collections) still run one-shot `SteamCef` evals with three hand-rolled
      residency sentinels and lifecycle smeared across `ShellSession`, beside the generation-2
      transport/patch-manager/bridge that carries everything new. Inventory, verified-primitive
      catalog, named debt (six webpack taps, five ownership-marker styles, the append chain, the
      3160-line bootstrap, the else-if request router), and the four-phase migration —
      bootstrap-internal first, library tabs last — in `_plan\cef-simplification.md`. **What
      remains is all of it** — the plan document is the only artifact.
- [ ] **Q17 — complete UI redesign of the WSGM overlay.** S10's structural completion stands —
      navigation, page stack, focus memory, one service per action — but the presentation does not
      match the maintainer's vision, and the **Device tab is the named mess**: it is a projection
      of capability lists onto cards rather than a designed surface, and needs fundamental
      restructuring, not another pass of polish. The first artifact is the design itself: capture
      the envisioned structure with the maintainer in `_plan\overlay-redesign.md` — what each
      destination presents, what the Device tab leads with versus what stays behind a page, how
      plugin-published capabilities become something a person on a couch parses at a glance — and
      only then rebuild against it. Rebuild on the existing owners (`OverlayNavigation`, the
      one-service-per-action rule, `DeviceOverlayBridge` as projection): the redesign changes what
      is drawn, never who owns the state. The S10 attended acceptance re-runs after this lands.
      **What remains is all of it, including the design document — the vision is not yet written
      down, and building ahead of it is how the current Device tab happened.**
- [ ] **Q18 — Avalonia UI testing framework, so UI work verifies itself without the maintainer.**
      The point is autonomy: an agent building UI must be able to see and exercise it — today every
      layout, focus and rendering question ends in "check it on the device", which makes the
      maintainer the test runner. Land this BEFORE Q17's rebuild phase, so the redesign is verified
      as it is built. Three pieces, all on Avalonia's own headless platform
      (`Avalonia.Headless.XUnit` — the full framework with no compositor, simulated input, and
      Skia-backed frame capture; tests run under CoreCLR, so NativeAOT is not in the way):
      - **Headless interaction tests** in the existing xUnit suite: construct Settings pages and
        overlay surfaces through the seams that already exist (`SettingsViewModel`'s internal
        `AppConfig` ctor, `OverlayController` previewOnly, the synthetic Device Lab plugin's
        manifest), drive them with simulated keyboard and with canonical button edges fed straight
        into `UiInputRouter`/`GamepadNavigation` — which makes the couch questions executable:
        focus lands where it should, Back pops what it should, a slider drag changes the one value
        it claims, focus memory restores. The curve editor and the plugin settings page are the
        first targets, then every Q17 page as it is rebuilt.
      - **A screenshot harness for development, not only pass/fail**: a safe local mode that
        renders a named page/destination to PNG at handheld resolution and scale, both theme
        variants, so an agent can look at what it built and iterate without a device round-trip.
        This is tooling beside the tests, not a test itself.
      - **Visual-regression baselines** where they earn their keep (theme tokens, shared controls,
        the redesigned Device tab), with determinism handled deliberately — pinned fonts, fixed
        scale, fixed size — because a flaky pixel diff is worse than none.
      The test-harness rule binds fully: no `%LOCALAPPDATA%\WSGM`, no `--shell`/`--boot`, `Log`
      stays uninitialized, everything through injected config and temp dirs. Attended device
      acceptance stays what it is — this replaces the maintainer as the *first* checker, not as the
      release gate. **What remains is all of it.**
- [ ] **Q19 — spike: the rest of Chrome DevTools against Steam's CEF, to make CEF work easier.**
      Everything today rides one CDP method — `Runtime.evaluate` with hand-written probe scripts —
      while the same port serves the protocol's full surface and the DevTools frontend itself.
      Evaluate what earns adoption into `tools/WsgmLibTest` and the dev workflow, in this order of
      expected payoff:
      - **`Page.captureScreenshot` on the MainWindow target** — the CEF twin of Q18's screenshot
        harness: capture the QAM/Settings surface as a PNG so an agent sees the grey slider, the
        missing row, the wrong order itself, instead of asking the maintainer. First concrete
        experiment; if it works, wire it into the harness as `qam-harness screenshot`.
      - **`DOM.*`/`CSS.*` domains** — inspect the rendered tree and matched styles directly rather
        than reconstructing them through evaluated JS; would have shortened the glyph-CSS and
        row-hiding battles considerably.
      - **The DevTools frontend for humans** — `http://127.0.0.1:8080` in a browser gives the full
        inspector against any target; document the workflow (which target is which, the
        SharedJSContext vs MainWindow split) in the harness README so it stops being tribal
        knowledge.
      - **`Debugger` domain + source maps for the bootstrap** — breakpoints in the injected
        TypeScript instead of `status()` archaeology; check whether the asset build can emit a
        source map without breaking the hash/drift gate (dev-only emission if need be).
      - **React DevTools backend injection** — component-tree inspection for the panel-wrap and
        row-mount work; strictly a dev-machine tool, never shipped.
      Constraints stand: the port stays loopback-only, the destructive-probe rules in
      `docs/steam-cef.md` bind (inspection is read-only; never iterate the module registry
      constructing exports), and nothing here ships in the product — this is developer/agent
      tooling beside Q16, whose findings land in the harness README and `docs/steam-cef.md`.
      **What remains is all of it.**

A checked architectural queue item has its code, focused tests, diagnostics, and documentation
complete. Attended/live gates remain explicit and unchecked until they run on the reference device;
they are release evidence, not a reason to leave finished source architecture open. Do not add
hundreds of sub-gates; add a concrete bug or missing outcome to the owning item.

## Simplification completion criteria

The structural overhaul is complete when:

- Every fixed feature still has a direct owner and implementation path.
- Normal startup has exactly the 0/1/2+ plugin behavior above.
- A community developer can understand the public SDK and package format quickly.
- WSGM, DeviceHost, Device Lab, and the Claw plugin no longer carry multi-plugin/trust/evidence/
  promotion machinery.
- UI actions reach one service without parallel policy/projection stacks.
- Every normal desktop handoff yields an initialized, medium-integrity, current-session Explorer
  that is not associated with a job; scheduled-task Explorer launch is recovery-only and reported
  as degraded.
- The solution/build/installer contain only boundaries justified by AOT, process lifetime, public
  SDK ownership, or packaging.
- Safety remains concentrated around exact hardware writes, input recovery, CEF fail-open, and
  cleanup — not spread across enterprise governance abstractions.

The current source/build composition meets those structural criteria. Full 2.0 completion remains
separate: the feature set must still pass its focused automated, live Steam, and attended
reference-device validation and produce the release installer artifact.
