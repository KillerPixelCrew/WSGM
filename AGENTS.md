# WSGM contributor guide

The maintainer's current instructions win over guides, plans and skills. A nearer AGENTS.md adds
domain rules. Deliver the requested implementation and honest proof; process must shorten delivery.
The validation budget below overrides older gate recipes in skills and documentation.

## Delivery

- Finish the requested backend, controls, runtime transitions, dependencies and both UI surfaces.
  Plans, metadata and fixtures are not implemented features. Correct the responsible owner;
  reuse existing services and avoid speculative frameworks or repeated review handoffs.
- Preserve unrelated changes. Give workers separate writable paths and bounded outcomes. One owner
  runs shared formatting, builds and deployment; do not run those concurrently.
- Continue an already authorized task branch. Otherwise commit and push to `master` here and
  `wsgm` in external/viiper. Create no branch, worktree, clone or PR without direction. Push changed
  submodules before recording their gitlinks.
- An approved implementation needs no second permission round. For an authorized development
  deployment use eng/dev-deploy.ps1; it owns stopping WSGM/Steam, swapping files and restarting.
  Do not ask the maintainer to close WSGM manually or overwrite a running application's files.
- "Publish a release" means building the full setup locally with build.ps1. GitHub tags/releases
  need an explicit request. Report applied code, actual checks, delivered artifacts and remaining
  acceptance briefly. Builds and fixtures do not prove live Steam, visible UI or hardware behavior.

## Source and ownership

- Start cross-project work with `.agents/skills/wsgm/SKILL.md`; it maps owners, reusable elements
  and specialist skills. `.claude/skills` aliases those canonical files. Tracked files and WSGM.slnx
  define the topology; retired projects under untracked output are not source.
- docs/README.md starts documentation, docs/decisions.md owns product decisions, and
  `_plan/implementation-todo.md` tracks progress. `_plan/2.0-decisions.md` is outdated.
- `src/WSGM` owns application policy/UI. Launch de-elevates and holds input leases; PackagedLaunch
  launches imported packaged games; LogonService starts WSGM at logon; Setup installs.
  WSGM.Plugin.Sdk is the common MIT extension contract. Read IR's README/protocol.md before editing it.
- LibHandheld owns exact detection, protocols and physical input; LibGPUDriverInteract owns GPU
  drivers; WindowsDeviceControl owns Windows primitives; SteamUiToolkit owns Steam UI mechanics.
  These and other pinned/vendored dependencies live in `external/`. Keep WSGM adapters/policy in WSGM.
- Device Lab collects evidence and scaffolds LibHandheld contributions. Its read-only
  LibreHardwareMonitor telemetry is separate from runtime handheld control.
- Search `_ref` with `rg --hidden --no-ignore`; it is evidence, not build input. Its decompiled HC
  1.3.1.6 is newer than public source. HC is primary for Windows and ROG Ally, buttons included;
  HHD cross-checks gaps. Dated hardware notes are historical evidence, never fresh passes.
- Target `net10.0-windows`; use `net10.0-windows10.0.19041.0` only for WinRT or WindowsDeviceControl.
  When moving a feature, account for every declaration in its old home.

## Product invariants

- Run at most one exact LibHandheld definition. Device Integration off means no device lifecycle,
  controller target, hardware writes or AutoTDP; independent features keep working.
- Never gate writable controls or commands on readback. Write as HC does and publish accepted
  values. Never automatically retry an uncertain write; resolve its state or require a user action.
- High-rate input and telemetry allocate and log nothing per sample. Remote testers are ordinary
  users: diagnose from wsgm.log and reference source, never ask them for developer probes.
- Settings configures WSGM. Windows/external-state controls belong in Overlay and Steam Quick
  Access, subject to the two exceptions in docs/decisions.md. Every Big Picture feature has Overlay
  parity through the same backend, actions and state, using each surface's established controls.

## Validation budget

- Manual testing comes first: build, perform the authorized deployment and let the maintainer try
  it. Tests/coverage wait for that report unless requested sooner. A manual regression report
  already satisfies this condition for its fix.
- Choose one useful batch from the diff: warning-free Release compilation, relevant tests and
  affected asset/UI checks. Reuse successful results while their inputs are unchanged. Committing,
  pushing or a documentation-only edit does not invalidate code checks.
- Do not automatically run eng/verify.ps1 for an initial feature, follow-up, PR or push. The full
  gate needs an explicit request, or a change to shared validation infrastructure that requires it.
  Name that concrete reason before starting; "there is a PR" or "broad impact" alone is insufficient.
- A requested full gate owns formatting, compilation, tests and coverage. Never precede it with a
  separate solution-wide cleanup and another complete build/test batch. If those checks already
  passed on the same source, reuse that proof instead of launching a duplicate gate merely for
  a committed-head result. Fix isolated failures and rerun only their affected checks.
- Rider Full Cleanup remains the C# authority. Keep the whole project clean and formatted, but
  do not launch cleanupcode for every small edit. When whole-project cleanup is requested, run it
  once over src/tests for the coherent increment; do not repeat it inside another validation pass
  on unchanged C#. Ordinary work does not implicitly request it. Match existing style for tiny fixes.
- C#: expanded braces, `var` locals, no trailing commas in multiline lists, explicit `new` types
  when the target is not evident, named arguments on literals. Overrides live in .editorconfig;
  WSGM.slnx.DotSettings supplies Rider's profile settings.
- Format changed Prettier-owned files before committing. Avoid repository-wide formatting for a
  small Markdown/JSON/JS edit. Instruction-only edits need no Rider, compilation or full gate.
- For changed UI layout, render affected cases, review images, promote only named baselines with
  eng/update-ui-baselines.ps1, then rerun those cases. VIIPER changes require
  eng/build-viiper.ps1 -Validate; build scripts retain their required native validation.
- Do not enable or poll CI. Long commands get one tracked execution and bounded completion waits;
  retain raw output in artifacts and return compact results. Do not repeatedly read unchanged log
  tails, narrate polls or spend reasoning tokens waiting. Delegate a long build/check batch when
  useful so the main chat stays available. A slow command gets one targeted health check.
- Record source/artifact identities and proof limits once. State deferred tests; never call an
  interrupted check a pass or lower an acceptance requirement to fit a budget.

## Live safety and maintenance

- Opening Settings and --overlay-test are safe. Early restore-shell must work without config,
  logging, Avalonia or GPU initialization. Shell/boot changes, plugin/service installation,
  Device Lab actions and deployment require the maintainer's direction; authorization persists.
- Before a live CEF connection, confirm from this run's Steam logs that Steam and Big Picture fully
  started. Endpoint reachability/a visible window is insufficient. Follow the Steam debugging skill;
  an incomplete preflight authorizes neither connecting nor force-closing Steam.
- Inspect known Steam modules by literal ID. Never sweep the registry, instantiate unknown exports
  or evaluate arbitrary JS. tools/WsgmLibTest scripts and close_page are attended mutations.
- CLAUDE.md symlinks to its sibling AGENTS.md. Edit AGENTS.md only and run the lightweight
  eng/check-agent-guidance.ps1 after guidance changes.
- build.ps1 stages the complete setup. src/WSGM/WSGM.csproj owns the release Version; its fourth
  component is the commit count so prerelease installers update each other. Never bump Version
  for a test build. Write natural, concise prose without canned AI phrasing or em dashes.
