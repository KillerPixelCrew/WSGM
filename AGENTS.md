# WSGM contributor guide

This file applies to the whole repository. A nearer AGENTS.md adds rules for its subtree and wins
when the guidance conflicts. The maintainer's explicit task instructions take precedence over
repository guidance, plans and skills, including branch and pull-request instructions.

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
  src/WSGM.PackagedLaunch is the launcher an imported Xbox, UWP or MSIX shortcut points at; it is a
  sibling of WSGM.Launch, not an extension of it. src/WSGM.LogonService is the minimal SYSTEM
  service used at logon.
- external/ holds all upstream code and pins. external/steam-input-lease owns the Steam Input shim,
  external/windows-device-control and external/steam-ui-toolkit own their reusable libraries, and
  external/viiper is the VIIPER fork WSGM builds. external/LoadingIndicators.Avalonia is vendored
  source, and external/controller holds the controller dependency lock, licences, and notes.
- src/WSGM.Plugin.Sdk holds common plugin contracts. The resident Shell host admits the existing
  Device runtime through an adapter and independently manages explicitly enabled non-device packages.
  Status is tracked in _plan/implementation-todo.md.
- src/WSGM.Plugin.Ir owns the independent IR integration and its Firmware subtree. It is under
  development; read its README and protocol.md before endpoint work. Firmware builds do not prove
  live learn/transmit behavior, and carrier metadata must distinguish assumptions from measurements.
- src/WSGM.Device.Sdk is the device contract. src/WSGM.DeviceLab is the hardware
  validation tool. src/WSGM.Device.Msi.Claw8A2Vm is the machine-specific package.
  src/WSGM.Device.HandheldCompanion is a design scaffold, not a working plugin.
  src/WSGM.Device.Asus.RogAllyX is a passive scaffold that never matches hardware yet.
  For Ally X work, use HHD as the primary implementation reference, especially for buttons; the
  maintainer reports buggy HC button handling. An attended remote tester is available; the portable
  tool in tools/AllyXLab records evidence but does not establish production support.
  Their tests live under tests, and WSGM.slnx builds them against one SDK project.
- WSGM supports exactly one installed device integration package at a time. With device integration
  disabled, there is no Device plugin lifecycle, controller target, Device hardware write, or AutoTDP;
  device-independent core, explicitly enabled common plugins and RTSS features must continue to work.
- Keep policy and orchestration in WSGM, reusable contracts in the SDK, and machine-specific
  behavior in the device package. Device projects are maintained together in this repository;
  keep their assembly and license boundaries intact.
- The SDK is MIT-licensed deliberately so external packages can implement its contracts. That
  narrower license boundary does not change the main product's GPL licensing.

## Working rules

- WSGM Settings configures WSGM itself only. Controls that change Windows or other external system
  state belong on the overlay's relevant page or in Steam QAM, not in WSGM Settings. Windows power
  schemes belong on the overlay's Device page, even when Device Integration is off.

- Inspect git status before changing anything. Preserve unrelated edits and never clean, reset, or
  rewrite user work to make a task easier.
- When a maintainer has approved a requested implementation and an applicable design or plan already
  exists, begin implementation immediately. Treat that approval as sufficient for the execution
  method; continue without asking for another plan review or execution-choice prompt.
- Preserve the task branch and use the branch/PR workflow below. These rules also apply to nested
  repositories and dependency pin-only updates; verify dependency commits are already pushed.
- Do not create tags, releases, or compatibility layers unless the maintainer asks for them.
- Write documentation, command examples, issues, commit messages, and pull requests in natural,
  concise language. Avoid canned AI phrasing, filler, and em dashes.
- Run `npm run format` before committing whenever the change touches a file Prettier owns, which
  includes every Markdown, JSON, YAML, CSS and JavaScript file. Editing a paragraph usually leaves
  the surrounding lines rewrapped, and `prettier --check` is the first thing eng/verify.ps1 runs, so
  an unformatted Markdown edit fails CI before the build or tests start. This applies to
  documentation-only changes.
- Prefer the smallest direct design that preserves established behavior. Remove dead paths instead
  of keeping speculative abstractions.
- Moving a feature between projects is a move, not a rewrite. Before calling one done, enumerate
  what the old home declared - every setting, action, contribution, event and lifecycle hook - and
  name where each one now lives or why it is gone. A file that is dissolved rather than moved is
  where the losses hide: the artwork fold dropped eight of twelve settings and the configuration-
  changed hook that way, and both shipped because nothing compared the two surfaces.
- Rider's formatter is the C# layout authority. Its Full Cleanup profile (the same ReSharper
  engine that `jb cleanupcode` and `jb inspectcode` run) defines the layout, including
  expanded braces and the JetBrains recommended style: `var` for locals, no trailing commas in
  multiline lists, explicit types on `new` when the target type is not evident. eng/verify.ps1
  runs that cleanup over src and tests and fails on any diff; `-Fix` applies it. `dotnet format`
  keeps only its style and analyzer passes, and Roslyn's IDE0055 is off. WSGM.slnx.DotSettings
  carries the shared inspection overrides; keep named arguments on literal values.
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

### Branch ownership and publishing

- At task start, record the current branch, upstream and working-tree status. Continue on the
  maintainer-selected branch; an existing non-default checkout is the task branch unless directed
  otherwise. Preserve its name even when it does not use the usual branch prefix.
- Use a task branch and PR into the intended base by default. If starting on the default branch or
  detached HEAD with no selected task branch, create a task branch from the current commit before
  making changes. Do not switch an existing task branch to the default branch to satisfy a plan,
  skill or general workflow preference.
- Before every commit and push, verify that the checked-out branch and explicit push destination
  match the task branch. A request to "push" or "create a PR" means publish that branch and open or
  update its PR; it does not authorize merging or writing to the default branch. Direct commits or
  pushes to the default branch require an explicit maintainer instruction for the current task.
- Before creating a PR, verify its head, intended base and actual diff. If the change is already on
  the base branch, report that state and obtain repair direction before changing branches or
  history. Do not manufacture review-base branches or substitute a different base to produce a PR.
- Keep a pull request reviewable. A diff nobody can hold in their head does not get reviewed
  properly, by a person or by a model. Split work that has natural seams into cohesive dependent
  PRs; each stack base must be the branch of a real preceding PR. Preserve the maintainer's task
  branch as the final head and document the merge order.
- Reverting shared commits, rewriting published history, deleting remote branches or moving work
  to another branch requires explicit maintainer direction. When a Git mistake occurs, report the
  exact local and remote state and propose a concrete repair before making further Git mutations.
  Carry out an already authorized repair without asking again.
- Leave the workspace on the task branch. After pushing, verify its upstream matches and report
  the actual branch and PR, with any remaining work or unrelated edits stated accurately.

### Submodule ownership

Inspect both the main tree and nested repositories before work:

    git status --short --branch
    git submodule status --recursive

The direct submodules are:

- external/steam-input-lease
- external/steam-ui-toolkit
- external/viiper
- external/windows-device-control

The device projects use src/WSGM.Device.Sdk directly. Update contracts, consumers, tests, and
documentation in the same WSGM pull request. No device gitlinks or nested SDK copies remain.
Synchronize or fetch the remaining submodules only when the task requires current remote state;
never use an update command to overwrite local submodule work.

For a remaining library or Steam Input submodule change, commit and push the child before
recording its gitlink in WSGM. Do not run a submodule update after moving a child until the intended
gitlink has been staged or committed.

Before reporting a push complete, confirm the intended files only were committed, each repository is
clean apart from preserved unrelated edits, and each branch pushed for this task equals its upstream.
Leave unrelated local branches and detached submodule checkouts unchanged.

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

Manual testing comes first in the local development loop. Build and, when explicitly requested,
deploy the change promptly so the maintainer can try it. Run automated test suites only after the
maintainer reports having tested that change manually, unless they explicitly request tests sooner.
This includes focused suites, full suites, coverage, and test-bearing gates such as eng/verify.ps1
and Steam asset ownership claims. Writing regression tests may accompany implementation; executing
them waits. Compilation, asset generation/drift checks, formatting, syntax and guidance checks may
run before manual testing. Do not hold a requested development deployment or a commit to a local or
pushed task branch for test-suite completion. State which tests are deferred. CI stays unchanged.
Opening a pull request, or pushing to a branch that has one, is the exception: see "Before a pull
request is opened or updated".

This timing rule applies to scoped contributor guides and skills as well: their test and gate
instructions describe what to run after manual testing, not a prerequisite for the first deployment.

After manual testing, use the narrowest relevant test while iterating:

    dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Area"

After the maintainer's manual test, run the repository gate once for the initial implementation:

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
require `eng/build-viiper.ps1 -Validate`, which tests and builds the external/viiper submodule. The
following variant writes formatting changes and must be reviewed:

    .\eng\verify.ps1 -Fix

### Before a pull request is opened or updated

CI runs `eng/verify.ps1` on every push to a pull request, and a red run is a defect of the change,
not something to leave for the maintainer.

The full gate takes about twenty minutes, so run it once per pull request, not once per push.
Before `gh pr create`, and before a later push that changes anything under `src`, `tests`,
`external` or the build scripts:

1. Commit first, then run `.\eng\verify.ps1` on that exact branch head and push only when it passes.
   The layout step diffs the working tree, so any uncommitted change under `src` or `tests` fails
   it. For a stacked set, run it on the branch the change lands in, then merge that branch upward
   and push the rest without a second full run unless the merge itself changed code. For these
   pushes this section overrides the follow-up rule above.

   A push that touches only documentation, plans or guidance needs the formatting and guidance
   checks instead: `npm run format:check` and `eng/check-agent-guidance.ps1`. Never spend the full
   gate on prose, and never re-run it just because a review round produced another commit.
2. Never run a narrower form of a gate step and treat it as the gate. In particular, the Rider
   cleanup must run solution-wide, exactly as `eng/verify.ps1` runs it, never with an `--include`
   limited to the changed files. A change to a type can require cleanup in files the diff never
   touched: on 2026-09-22, removing an interface member left a test fake still implementing it, and
   cleanup reordered that untouched file.
3. After removing or renaming a public, internal or interface member, search `src` and `tests` for
   every implementer and caller, fakes included. The compiler does not flag a class that still
   implements a method its interface no longer declares; delete the leftover rather than letting
   cleanup reorder it.
4. After changing overlay layout, run `eng\update-ui-baselines.ps1` for the affected cases and review
   every changed image before committing it.
5. Do not sit in a polling loop on the pull request's checks. The local gate already ran, so
   report the branch and pull request, say that CI is still running, and hand the turn back.
   Check the result once when there is a reason to return to that branch, and fix a real failure
   then.

This supersedes the manual-first deferral for the pull-request step only. Deploying and committing
for the maintainer's manual test still come first and do not wait for the gate.

Use build.ps1 only when an installer or full release staging is required. It builds the Steam
assets, native components, all three applications, staged device/controller payloads, and the Inno
Setup installer:

    .\build.ps1

The Version property in src/WSGM/WSGM.csproj is the release version source. build.ps1 passes it to
Inno Setup; keep the installer's direct-ISCC fallback and the app manifest identity aligned without
copying a version into contributor guidance. eng/check-version-sync.ps1, run by build.ps1 and
eng/verify.ps1, fails when they drift. The installer is written under publish with the version in
its filename.
