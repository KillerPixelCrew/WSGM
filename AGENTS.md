# WSGM contributor guide

This file applies to the whole repository. A nearer AGENTS.md adds rules for its subtree and wins
where the two conflict. The maintainer's task instructions win over every guide, plan and skill,
including on branches and pull requests. The maintainer works alone on this repository and reviews
commits on the default branch.

## Sources of truth

- Tracked files and WSGM.slnx are the topology. Ignore retired projects that survive only under
  bin, obj, publish or other untracked output.
- `_ref` holds local reference sources and is evidence, not part of the build. `_ref/HandheldCompanion`
  is a decompiled Handheld Companion (HC) 1.3.1.6 build, newer than HC's public source; its
  PROVENANCE.md says how it was made. Use these local sources first, search them with
  `rg --hidden --no-ignore`, and fetch or clone another copy only when a task needs newer upstream
  evidence.
- Documentation starts at docs/README.md. Product decisions live in docs/decisions.md;
  `_plan/2.0-decisions.md` is the outdated planning record and is not cited.
- `_plan/implementation-todo.md` is the progress tracker. `_plan/implementation-requirements.md` is
  an invariant inventory, not a second tracker; where it still names an attended release gate, the
  tracker and the maintainer's decision govern.
- Code and tests define implemented behavior. A dated hardware note is evidence from that day; never
  present one as a fresh live pass unless you ran the named scenario and recorded the result.

## Repository shape

- `src/WSGM` is the self-contained CoreCLR desktop application: the Explorer-replacement session,
  UI, overlay, settings, recovery and per-user state.
- `src/WSGM.Launch` is the console launcher for de-elevation and input-lease containment.
  `src/WSGM.PackagedLaunch` is the launcher an imported Xbox, UWP or MSIX shortcut points at, a
  sibling of WSGM.Launch, not an extension. `src/WSGM.LogonService` is the SYSTEM service used at
  logon. `src/WSGM.Setup` is the installer.
- `external/` holds upstream code and pins: `steam-input-lease` (the Steam Input shim),
  `windows-device-control` and `steam-ui-toolkit` (reusable libraries), `viiper` (the VIIPER fork
  WSGM builds), `LoadingIndicators.Avalonia` (vendored source) and `controller` (the controller
  dependency lock, licences and notes).
- `src/WSGM.Plugin.Sdk` holds the common plugin contracts. The resident Shell host admits the Device
  runtime through an adapter and manages explicitly enabled common packages on its own.
  `src/WSGM.Plugin.Ir` is the independent IR integration with its Firmware subtree, under
  development: read its README and protocol.md before endpoint work, and remember that a firmware
  build does not prove live learn or transmit behavior.
- `src/WSGM.Device.Sdk` is the device contract, `src/WSGM.DeviceLab` the hardware validation tool,
  `src/WSGM.Device.Msi.Claw8A2Vm` the Claw package. `src/WSGM.Device.HandheldCompanion` is a design
  scaffold, not a plugin. `src/WSGM.Device.Asus.RogAlly` covers the ROG Ally, Ally X, Xbox Ally and
  Xbox Ally X, built blind from HHD and HC and awaiting Device Lab evidence; its PROVENANCE.md
  lists what a lab report must confirm. For Ally work HC is the primary reference, buttons
  included; cross-check HHD and use it where HC has no answer. The Device Lab wizard
  (`wsgm-device`) records evidence from an attended remote tester but does not establish support.
- WSGM runs exactly one installed device package. With device integration off there is no Device
  plugin lifecycle, controller target, device hardware write or AutoTDP, and everything
  device-independent, including enabled common plugins and RTSS, must keep working.
- Policy and orchestration live in WSGM, reusable contracts in the SDKs, machine-specific behavior
  in the device package. Keep the assembly and licence boundaries: the SDKs are MIT on purpose so
  external packages can implement them, and that does not change the product's GPL licence.

## Working rules

- WSGM Settings configures WSGM itself. A control that changes Windows or other external state
  belongs on the overlay's relevant page or in Steam's Quick Access, not in Settings; Windows power
  schemes belong on the overlay's Device page even with device integration off. The two recorded
  exceptions are in docs/decisions.md.
- Inspect `git status` before changing anything. Preserve unrelated edits; never clean, reset or
  rewrite the maintainer's work to make a task easier.
- Once the maintainer has approved an implementation and a design or plan exists, implement it.
  Do not ask for another plan review or an execution-method choice.
- Never create a worktree, clone or task branch on your own initiative. Ask first, say what it
  costs, and remove it after the merge.
- Prefer the smallest direct design that keeps established behavior. Delete dead paths rather than
  keeping speculative abstractions. Do not add tags, releases or compatibility layers unless asked;
  "publish a release" means building the setup locally with build.ps1, never a GitHub tag or
  release.
- Moving a feature between projects is a move. Before calling it done, list everything the old home
  declared (settings, actions, contributions, events, lifecycle hooks) and name where each now
  lives or why it is gone. Dissolving a file instead of moving it is where losses hide: the artwork
  fold shipped with eight of twelve settings and a lifecycle hook missing because nothing compared
  the two surfaces.
- A plan states the scope and the finished product before it lists edits: what the feature is for,
  which surfaces reach it, its components and their owners, and what a user can do when it is
  complete. Before calling the work done, check the code against that description point by point
  and report every gap. Compiling and passing tests is not that check.
- Never report a feature as done while any part is a stub or placeholder. When part of the scope is
  blocked, finish the rest and say exactly what is missing and why.
- Remote testers are ordinary users. Diagnose from wsgm.log and the HC source; do not ask them to
  run probes or tools.
- An uncertain device or capability write is never retried automatically. Re-read state or require
  an explicit user action before another write. Never gate a write or a UI control on readback:
  write as HC does and publish the written value as observed.
- Keep nullable analysis, build-time code-style checks and public XML documentation clean. Never
  block the UI thread; UI-observable state lives on the Avalonia dispatcher; long-lived work has
  explicit ownership, cancellation and disposal. High-rate input and telemetry paths allocate
  nothing and log nothing per sample.
- Rider's formatter is the C# layout authority: its Full Cleanup profile, the same ReSharper engine
  `jb cleanupcode` and `jb inspectcode` run, with expanded braces and the JetBrains recommended
  style (`var` for locals, no trailing commas in multiline lists, explicit types on `new` when the
  target type is not evident). eng/verify.ps1 runs it over src and tests and fails on any diff;
  `-Fix` applies it. `dotnet format` covers only its style and analyzer passes, and IDE0055 is off.
  WSGM.slnx.DotSettings carries the shared inspection overrides; keep named arguments on literal
  values.
- Write documentation, commands, issues, commit messages and pull requests in natural, concise
  language, without canned AI phrasing, filler or em dashes.
- Run `npm run format` before committing whenever the change touches a file Prettier owns: every
  Markdown, JSON, YAML, CSS and JavaScript file, documentation-only changes included.
  `prettier --check` is the first thing eng/verify.ps1 runs.
- Before every commit, reconcile the implementation with every affected README, the scoped
  AGENTS.md files, the relevant docs and plans, and any skill instruction files. Fix or remove stale
  guidance in the same commit.
- CLAUDE.md files are tracked relative symlinks to their sibling AGENTS.md. Edit AGENTS.md only, run
  eng/check-agent-guidance.ps1 after adding or moving a scope, and use a symlink-capable checkout on
  Windows rather than copied files.

## Git and submodules

- Work on the default branch and push directly: `master` in WSGM, `wsgm` in external/viiper, the
  default branch of each other submodule. No task branch, worktree or pull request unless the
  maintainer asks in the current task; if they have checked out another branch, stay on it.
- Commit as the work reaches usable states, one coherent change with its documentation per commit,
  and keep unrelated edits out. Report what was committed and pushed, and what was left.
- Never rewrite published history, revert shared commits or move work between branches without
  direction. On a Git mistake, report the exact state and propose a repair before mutating further.
- Inspect the main tree and the nested repositories before work:

      git status --short --branch
      git submodule status --recursive

  Commit and push a submodule child before recording its gitlink in WSGM, and never run a submodule
  update that would overwrite local work or a gitlink you have not staged. The device projects use
  src/WSGM.Device.Sdk directly; a contract change, its consumers, tests and docs land in one commit.

## Safety boundaries

- Opening Settings and `--overlay-test` are non-destructive. The early restore-shell path must keep
  working without config, logging, Avalonia or GPU initialization.
- Shell and boot modes, plugin install or removal, service installation, Device Lab hardware actions
  and eng/dev-deploy.ps1 change the live machine. Run them only with explicit direction and the
  required recovery path.
- The Steam CEF configurations connect to the user's live Steam session on loopback. Literal, known
  module inspection is acceptable when requested. Never sweep the module registry, instantiate
  unknown exports or evaluate arbitrary JavaScript as a probe.
- tools/WsgmLibTest/run-file.mjs and qam-harness.mjs can mutate live Steam, and close_page closes
  Steam's real window. They are attended tools, not validation.

## Validation

Manual testing comes first. Build promptly, deploy when the maintainer asks, and let them try the
change. Run automated tests only after the maintainer reports having tested manually, unless they
ask for tests sooner. That covers focused suites, full suites, coverage and every test-bearing gate,
eng/verify.ps1 and the Steam asset ownership claims included. Compilation, asset generation and drift
checks, formatting, syntax and guidance checks may run before the manual test. Writing regression
tests may accompany the implementation; running them waits, and the report says which tests were
deferred. Scoped guides and skills describe their tests under the same timing. CI is unchanged.

After the manual test, iterate with the narrowest relevant test:

    dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Area"

Run the repository gate once for the initial implementation:

    .\eng\verify.ps1

For follow-up fixes on an already verified change, run only the tests and checks the diff affects.
Do not rerun the full suite, coverage or eng/verify.ps1 (including `-Fix`) for another review round;
once the maintainer has said to skip the gate, later pushes skip the Rider cleanup and tests too.
Documentation-only follow-ups need the formatting and guidance checks, nothing more. Repeat the
full gate only when a change has broad impact, touches shared build or test infrastructure or
dependency versions, or a failure cannot be isolated, and say why before running it. Report an
earlier gate result as what it was, never as a fresh run.

eng/verify.ps1 checks formatting, generated Steam assets and ownership claims, guidance links,
PowerShell syntax, live-data exclusions, dependency pins, Steam Input validation, restore,
warning-clean Release builds, tests and coverage. It does not validate VIIPER; a VIIPER change needs
`eng/build-viiper.ps1 -Validate`. `.\eng\verify.ps1 -Fix` writes formatting changes that must be
reviewed.

### When the maintainer asks for a pull request

Deliver one complete pull request, not a stack, unless a stack is asked for. CI runs eng/verify.ps1
on every push, and a red run is a defect of the change, so before `gh pr create` and before any
later push that changes src, tests, external or the build scripts: run the solution-wide Rider
cleanup exactly as the gate runs it and review its diff, run `npm run format`, build Release
warning-free, commit, run `.\eng\verify.ps1` on that head and push only when it passes. A push that
touches only documentation or guidance needs `npm run format:check` and eng/check-agent-guidance.ps1
instead. After an overlay layout change, refresh the affected baselines with
`eng\update-ui-baselines.ps1` and review every image. Do not poll the PR's checks; report the PR and
hand the turn back.

### Release builds

Use build.ps1 only for a setup or full release staging. It builds the Steam assets, native
components, the three applications, the plugin bundle and the controller payload, and publishes the
single-file setup under publish with the version in its filename:

    .\build.ps1

The Version property in src/WSGM/WSGM.csproj is the release version; WSGM.Setup reads it from there,
and eng/check-version-sync.ps1 fails when the app manifest drifts. The fourth assembly version part
is the build revision, the commit count of HEAD (eng/wsgm-revision.targets), so prerelease builds
update each other without a version bump. Never bump the Version for a test build.
