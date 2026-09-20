# Production overlay redesign implementation plan

**Goal:** Apply the command-deck mockup to the actual overlay and implement the whole scope of
issue 114.

**Architecture:** Keep Avalonia and the existing session, command, capture and lease owners. Replace
presentation with the mockup's fullscreen glass shell, persistent one-third section rail and
two-thirds controls pane, clustered header and unified app/tray rail. Utility, keyboard and power
surfaces live inside that window. Device descriptors remain markup-free and their rows reconcile in
place.

**Tech stack:** .NET 10, Avalonia 12.1.2, FluentAvalonia 3.1.0, Avalonia.Labs.Panels 12.0.2.

**Spec:** [Issue 114](https://github.com/KillerPixelCrew/WSGM/issues/114), with
`tools/OverlayMockup` as the visual and interaction reference. The earlier mockup-only plan's
production exclusion does not apply to this promotion.

## Constraints

- Preserve stable routes, pins, per-user configuration and existing command ownership.
- Keep device-independent controls reachable without Device Integration.
- Preserve 150 ms deferred closure and synthesized-mouse filtering.
- Use configured accent resources, 4-DIP control radii, at most 6-DIP group radii and visible 2-DIP
  focus.
- Build and inspect before attended testing. Do not execute suites or test-bearing gates until the
  maintainer reports manual testing or explicitly requests them.
- Keep the maintainer-created `chore/redesign-overlay` as the final branch in the review stack into
  `master`: #160 (contracts, 14 files), #161 (runtime/test code, 99 files), then #159 (baselines,
  documentation and preview, 48 files). Preserve ancestry and recheck counts when retargeting. Child
  repositories remain unchanged unless needed.
- Report actual hardware observations separately from implementation and simulated verification.

## Work and ownership

- [x] Swipe recognizer implementation: narrow boundary start, early inward motion, dominant inward
      travel, diagnostic summaries and regression traces; StripThickness is the physical-pixel
      bezel-start width. Numeric thresholds still require attended calibration.
- [x] Fullscreen glass shell: native blur with tint, opaque hierarchy, no sheet tap-outside
      dismissal, fixed close path and shared sizing.
- [x] In-window radio/audio/eject and keyboard: one navigation owner, bounded focus scope, focus
      return, remove raw tap hit testing and cross-window handoff.
- [x] Device rows: real editors and compact statistics, flex layout, useful group ordering and
      stable focus.
- [x] Power menu: centred in-window surface sharing the Power tab's actions, safe initial focus,
      desktop entry point and cancellation.
- [x] Rollout: all destinations and nested views use the command-deck styles; plain navigation
      buttons; remove CardButton, its theme and DescriptorStatusRow.
- [x] Row reuse: reconcile values by capability identity and descriptor generation; avoid
      telemetry-driven reconstruction.
- [x] SDK layout hints: closed prominence/pairing metadata, host consumption, Claw descriptors,
      contract documentation and tests.
- [x] Full PC keyboard: function keys, modifiers, navigation, numeric pad, native text selection,
      clipboard, undo/redo and length limits.
- [x] DPI ownership: preserve the desired scale during Game Mode's system-wide 100% scaling, use
      native desktop DPI once, and fit 720p without changing the saved preference.
- [x] Readability polish: clearly bordered groups, full-size section headings, dividers, consistent
      padding, natural pinned-section heights and 720p/4K visual review.
- [x] Rework all affected UI tests: fixtures, fullscreen geometry, section navigation, controls,
      pins, complete Claw captures, utility/keyboard/power surfaces, focus/selection, minimum
      viewport and reviewed visual baselines.
- [x] Integration: reconcile guidance, docs, tracker and tests; compile warning-clean, inspect
      representative exports and review the diff.
- [ ] Attended validation: real swipe/title-bar/slow-touch traces, native blur/battery saver,
      gamepad/touch/keyboard, device-off, utilities, power cancellation and live input handoff.
- [ ] After attended testing: core and device suites, full gate once for this broad implementation.
      The maintainer explicitly authorized UI suites and baseline updates before manual testing.

## Review focus

- Descriptor refresh cannot replace an active editor, lose pending input or change the selected
  section.
- Utility and keyboard closure restores the invoker and cannot release the sheet's capture
  prematurely.
- A power-menu short press cannot execute sleep beneath the menu; cancellation is the safe default.
- At the mockup's 980 by 640 DIP floor, close, utility controls, section navigation and bottom rail
  remain reachable.
- Reduced transparency retains clear surface hierarchy and configured-accent focus remains legible.

## Execution notes

The parent owns shell/navigation/theme rollout and final integration. Independent workers own
surface lifetimes, device rendering/contracts and swipe recognition in disjoint files. Compilation
may expose integration errors while those changes are in flight; only the integrated build is
delivery evidence.

The supported capture matrix covers 1280 by 720 through 3840 by 2160, with desktop scaling and a 980
by 640 logical layout floor. The keyboard has persistent function keys and PC modifiers, navigation,
clipboard chords, and a numeric-pad page. Header status labels collapse at narrow logical widths
while every utility remains reachable.

The integrated Release solution build passed with zero warnings and errors. All 200 UI tests passed
after reviewing and updating 33 visual baselines. Complete Claw captures cover the dense controls,
section gaps and heading/pin alignment at 720p and 4K. Rider cleanup, Prettier, guidance checks and
diff checks passed. Core/device suites and the full gate remain deferred until manual testing;
headless captures do not establish live hardware or Windows compositor acceptance.
