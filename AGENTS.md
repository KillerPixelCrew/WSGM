# WSGM contributor guide

This file applies to the whole repository. A nearer AGENTS.md adds rules for its subtree and wins
when the guidance conflicts.

## Sources of truth

- Treat tracked files and WSGM.slnx as the current topology. Ignore retired projects that exist only
  under bin, obj, publish, or other untracked output.
- `_ref` contains local reference source repositories, including the complete Handheld Companion
  (HC) source at `_ref/HandheldCompanion`. Use these local sources first for implementation
  comparisons. Search ignored reference trees explicitly with `rg --hidden --no-ignore`; do not
  fetch or clone another copy unless the task requires newer upstream evidence. Reference source
  is evidence, not part of WSGM's build topology.
- Start documentation work at docs/README.md. Product decisions live in docs/decisions.md and
  _plan/2.0-decisions.md.
- _plan/implementation-todo.md is the progress tracker. Do not infer status from requirements lists,
  prose, or raw checkbox totals.
- _plan/implementation-requirements.md is an invariant and coverage inventory, not a second status
  tracker. Where it still describes an attended release gate, the current tracker and maintainer
  decision govern completion.
- Current code and tests define implemented behavior. Historical hardware notes are dated evidence;
  never describe them as a fresh live pass unless you ran the named scenario and recorded the
  result.

## Repository shape

- src/WSGM is the self-contained CoreCLR desktop application. It owns the Explorer-replacement
  session, UI, overlay, settings, recovery, and per-user state.
- src/WSGM.Launch is the console launcher for de-elevation and input-lease containment.
  src/WSGM.LogonService is the minimal SYSTEM service used at logon.
- native/SteamInput owns the Steam Input shim. external/windows-device-control and
  external/steam-ui-toolkit own their respective reusable libraries.
- external/WSGM.Device.Sdk is the plugin contract. external/WSGM.DeviceLab is the hardware
  validation tool. external/WSGM.Device.Msi.Claw8A2Vm is the machine-specific package.
- WSGM supports exactly one installed device integration package at a time. With device integration
  disabled, there is no plugin lifecycle, controller target, hardware write, or AutoTDP;
  device-independent core and RTSS features must continue to work.
- Keep policy and orchestration in WSGM, reusable contracts in the SDK, and machine-specific
  behavior in the device package. Do not mirror submodule source into the main project.
- The SDK is MIT-licensed deliberately so external packages can implement its contracts. That
  narrower license boundary does not change the main product's GPL licensing.

## Working rules

- WSGM Settings configures WSGM itself only. Controls that change Windows or other external system
  state belong on the overlay's relevant page or in Steam QAM, not in WSGM Settings. Windows power
  schemes belong on the overlay's Device page, even when Device Integration is off.

- Inspect git status before changing anything. Preserve unrelated edits and never clean, reset, or
  rewrite user work to make a task easier.
- Work on a dedicated branch for changes and deliver them through a pull request so CodeRabbit
  can review them. The exception is dependency pin-only updates: these may be committed and pushed directly to
  the default branch, including in nested repositories. Verify the intended target commits are
  already pushed, follow the dependency order below, and keep unrelated changes out of those
  commits. Do not create tags, releases, or compatibility layers unless the maintainer asks for them.
- Write documentation, command examples, issues, commit messages, and pull requests in natural,
  concise language. Avoid canned AI phrasing, filler, and em dashes.
- Prefer the smallest direct design that preserves established behavior. Remove dead paths instead
  of keeping speculative abstractions.
- Keep nullable analysis, build-time code-style checks, and public XML documentation clean. Avoid
  blocking the UI thread; make ownership, cancellation, and disposal explicit for long-lived work.
- UI-observable state belongs on the Avalonia dispatcher. High-rate input and telemetry paths must
  avoid per-sample allocation and logging.
- An uncertain device or capability write must not be automatically retried. Re-read state or
  require an explicit user action before another write.
- Before every commit, reconcile the implementation with every affected README, the applicable
  scoped AGENTS.md files, relevant docs and plans, and any present or future skill instruction
  files. Correct or remove stale guidance in the same change; do not commit a workflow or behavior
  change with known contradictory instructions.
- CLAUDE.md files in this repository are tracked relative symlinks to sibling AGENTS.md files. Edit
  AGENTS.md only. Run eng/check-agent-guidance.ps1 after adding or moving a scope. On Windows, use a
  symlink-capable checkout rather than replacing links with copied files.

## Git and submodules

Inspect both the main tree and nested repositories before work:

    git status --short --branch
    git submodule status --recursive

The direct submodules are:

- external/WSGM.Device.Sdk
- external/WSGM.DeviceLab
- external/WSGM.Device.Msi.Claw8A2Vm
- external/steam-ui-toolkit
- external/windows-device-control
- native/SteamInput

DeviceLab nests the SDK. The Claw package nests both the SDK and DeviceLab, and its DeviceLab nests
the SDK again. Synchronize or fetch only when the task requires current remote state; never use an
update command to overwrite local submodule work.

For a cross-repository change, work leaf first. Commit and push each child before recording its
gitlink in its parent. The dependency order is SDK, then DeviceLab, then the Claw package, then
WSGM; independent library or Steam Input changes must likewise be pushed before the WSGM pin. Do not
run a submodule update after moving a child until the intended gitlink has been staged or committed.

Before reporting a push complete, confirm the intended files only were committed, each repository is
clean, and every local branch equals its upstream.

## Safety boundaries

- Opening the settings surface and the exact overlay-test mode is normally non-destructive. The
  early restore-shell path must remain usable without config, logging, Avalonia, or GPU
  initialization.
- Shell and boot modes, plugin install or removal, service installation, Device Lab hardware
  actions, and eng/dev-deploy.ps1 affect the live machine. Run them only with explicit maintainer
  direction and the required recovery path.
- The Steam CEF configurations connect to the user's live Steam session on loopback. Literal, known
  module inspection is acceptable when requested. Never sweep the module registry, instantiate
  unknown exports, or evaluate arbitrary JavaScript as a harmless probe.
- tools/WsgmLibTest/run-file.mjs and tools/WsgmLibTest/qam-harness.mjs can mutate live Steam.
  close_page closes the real Steam window. Treat all of them as attended tools, not generic
  validation.

## Validation

Use the narrowest relevant test while iterating:

    dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Area"

Run the repository gate once for the initial implementation before delivery:

    .\eng\verify.ps1

For follow-up fixes on an already verified change, run only the tests and checks affected by the
diff. Do not rerun the full suite, coverage, or `eng/verify.ps1` (including `-Fix`) just because
there is another review round or commit. Documentation-only follow-ups need formatting and
guidance checks, not application tests.

Repeat the full local gate only when a change has broad impact, changes shared build/test
infrastructure or dependency versions, or a failure cannot be isolated with focused checks.
State the reason before running it. Reuse the earlier gate result and report the focused checks
for the follow-up honestly; do not describe the earlier pass as a fresh full run. CI stays unchanged.
Scoped guidance and skills that mention the full gate follow this same rule.

eng/verify.ps1 checks formatting, generated Steam assets and ownership claims, guidance links,
PowerShell syntax, live-data exclusions, dependency pins, Steam Input validation, restore,
warning-clean Release builds, tests, and coverage. It does not validate VIIPER; changes there
require `eng/build-viiper.ps1 -Validate`, whose SourceRoot is force-checked out, hard-reset, and
cleaned, so use only a disposable source tree with no work to preserve. The following variant writes
formatting changes and must be reviewed:

    .\eng\verify.ps1 -Fix

Use build.ps1 only when an installer or full release staging is required. It builds the Steam
assets, native components, all three applications, staged device/controller payloads, and the Inno
Setup installer:

    .\build.ps1

The Version property in src/WSGM/WSGM.csproj is the release version source. build.ps1 passes it to
Inno Setup; keep the installer's direct-ISCC fallback aligned without copying a version into
contributor guidance. The installer is written under publish with the version in its filename.
