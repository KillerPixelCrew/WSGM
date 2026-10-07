# WSGM contributor guide

A nearer AGENTS.md adds rules for its subtree. The maintainer's task instructions win over every
guide, plan and skill. The maintainer works alone on this repository and reviews commits on master.

## Where things are

- Start cross-project implementation work with `.agents/skills/wsgm/SKILL.md`; it maps architecture,
  ownership, reusable elements and the specialist skills. The `.claude/skills` entries are aliases
  of those canonical skills.
- Tracked files and WSGM.slnx are the topology; retired projects survive only under untracked
  output. Documentation starts at docs/README.md, product decisions are in docs/decisions.md, and
  `_plan/implementation-todo.md` is the progress tracker. `_plan/2.0-decisions.md` is outdated.
- `_ref` holds local reference sources, searched with `rg --hidden --no-ignore`; it is evidence, not
  build input. `_ref/HandheldCompanion` is a decompiled Handheld Companion (HC) 1.3.1.6 build,
  newer than HC's public source. For the ROG Ally family HC is the primary reference, buttons
  included, and HHD the cross-check for what HC does not cover.
- `src/WSGM` is the application; `WSGM.Launch` de-elevates and holds input leases;
  `WSGM.PackagedLaunch` is the shortcut target for imported packaged games, a sibling of
  WSGM.Launch; `WSGM.LogonService` starts WSGM at logon; `WSGM.Setup` installs. `external/` holds
  the pinned submodules (steam-input-lease, steam-ui-toolkit, viiper, windows-device-control) and
  vendored code. `WSGM.Plugin.Sdk` and `WSGM.Device.Sdk` are the plugin contracts, MIT on purpose so
  outside packages can implement them; the product stays GPL. `WSGM.DeviceLab` is the hardware
  validation tool, `WSGM.Device.Msi.Claw` the reference package (every MSI Claw, hardware-tested on the Claw 8 AI+ A2VM, the rest from HC), `WSGM.Device.Asus.RogAlly`
  the Ally package built blind and awaiting Device Lab evidence, `WSGM.Device.HandheldCompanion` a
  scaffold. `WSGM.Plugin.Ir` is under development; read its README and protocol.md first.
- Projects target `net10.0-windows`; a project moves to `net10.0-windows10.0.19041.0` only when it
  uses WinRT or references WindowsDeviceControl.
- A dated hardware note in the docs is evidence from that day. Never present one as a fresh live
  pass unless you ran the scenario.

## Product rules

- WSGM runs exactly one installed device package. With device integration off there is no device
  lifecycle, controller target, hardware write or AutoTDP, and everything else keeps working.
- Policy and orchestration live in WSGM, contracts in the SDKs, machine-specific behaviour in the
  device package.
- WSGM Settings configures WSGM itself. Controls for Windows or other external state go on the
  overlay's relevant page or Steam's Quick Access; the two recorded exceptions are in
  docs/decisions.md.
- Every feature added to Steam Big Picture must also be available in the WSGM overlay, with the
  same capabilities, actions and state. Share the owning backend and policy; use each surface's
  existing controls and styles. Keep changes focused, reusable and straightforward to maintain.
- An uncertain device write is never retried automatically: re-read state or require a user action.
  Never gate a write or a control on readback; write as HC does and publish the written value as
  observed.
- High-rate input and telemetry paths allocate nothing and log nothing per sample.
- Remote testers are ordinary users. Diagnose from wsgm.log and the HC source; never ask them to run
  probes.

## How the maintainer works

- Commit directly to the default branch and push: `master` here, `wsgm` in external/viiper. No task
  branch, worktree, clone or pull request unless asked in the current task. Commit and push a
  submodule child before recording its gitlink.
- Once an implementation is approved and a plan exists, implement it; do not ask for another review.
- "Publish a release" means building the setup locally with build.ps1, never a GitHub tag or
  release. Do not add tags, releases or compatibility layers unless asked.
- Prose is natural and concise, without canned AI phrasing or em dashes. Run `npm run format`
  before committing anything Prettier owns (Markdown, JSON, YAML, CSS, JavaScript); it is the first
  thing eng/verify.ps1 checks.
- Rider's Full Cleanup profile is the C# layout authority (expanded braces, `var` for locals, no
  trailing commas in multiline lists, explicit types on `new` when the target type is not evident).
  eng/verify.ps1 runs it over src and tests and fails on any diff; `-Fix` applies it.
  Rider inspection overrides live in `.editorconfig` (`resharper_*_highlighting`);
  WSGM.slnx.DotSettings is the solution settings layer `jb cleanupcode` reads. Keep named arguments
  on literal values.
- CLAUDE.md files are symlinks to their sibling AGENTS.md. Edit AGENTS.md only and run
  eng/check-agent-guidance.ps1 after adding or moving a scope.
- When moving a feature between projects, enumerate what the old home declared and account for each
  item; a dissolved file is where losses hide.

## Safety

- Opening Settings and `--overlay-test` are safe. The early restore-shell path must work without
  config, logging, Avalonia or GPU initialization.
- Shell and boot modes, plugin install or removal, service installation, Device Lab hardware actions
  and eng/dev-deploy.ps1 change the live machine: only with explicit direction.
- The Steam CEF tools connect to the live Steam session. Inspect known modules by literal id when
  asked; never sweep the module registry, instantiate unknown exports or evaluate arbitrary
  JavaScript. tools/WsgmLibTest scripts and close_page mutate live Steam and are attended tools.
- Before any attended CEF debugging connection or live tool call, confirm from the current run's
  Steam logs that Steam and Big Picture have fully started. A reachable endpoint or visible window
  alone is not sufficient. Connecting earlier can hang the entire Steam UI and require Steam to be
  force-closed. If the log evidence is incomplete, do not connect; this does not authorize force-closing
  Steam. Follow .agents/skills/wsgm-steam-cef-debugging/SKILL.md for the preflight.

## Validation

Manual testing comes first: build, deploy when asked, and let the maintainer try the change.
Automated tests, coverage and every test-bearing gate wait until the maintainer reports having
tested manually, unless asked sooner; compilation, asset drift, formatting and guidance checks may
run before. Say which tests were deferred.

After the manual test, iterate with the narrowest filtered test
(`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Area"`), then run
`.\eng\verify.ps1` once for the initial implementation. Follow-ups run only what the diff affects;
once told to skip the gate, later pushes skip the Rider cleanup and tests too. The full gate repeats
only for broad impact, shared build or test infrastructure, dependency versions, or a failure that
cannot be isolated, and you say why first. VIIPER changes need `eng/build-viiper.ps1 -Validate`.

When a pull request is asked for: one complete PR. CI runs the gate on every push, so before
creating it or pushing code changes, run the solution-wide Rider cleanup, `npm run format`, a
warning-free Release build and `.\eng\verify.ps1` on the committed head. Refresh UI baselines with
`eng\update-ui-baselines.ps1` after overlay layout changes and review the images. Do not poll CI.

build.ps1 stages a full release setup. The Version in src/WSGM/WSGM.csproj is the release version;
the fourth part is the commit count, so prerelease builds update each other. Never bump the Version
for a test build.
