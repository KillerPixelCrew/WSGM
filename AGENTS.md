# AGENTS.md

Source of truth for every coding agent working in this repository (Claude Code, Codex, others).

**The agent instructions ship with the repo, deliberately.** Every `AGENTS.md` and every `CLAUDE.md`
is tracked, so review tooling and a contributor's own AI read the same binding rules from the checked
-out tree rather than guessing. Do not add either back to `.gitignore`.

**Every `CLAUDE.md` here is a symlink (mode `120000`) to the `AGENTS.md` beside it** — including the
one at the repository root. That is load-bearing, not tidiness: Claude Code auto-loads `CLAUDE.md`,
so the symlink is what puts these rules into the agent's context. A `CLAUDE.md` that is a *regular
file* — typically one line reading `AGENTS.md`, left by a checkout without symlink support or by a
tool that rewrote it — silently drops that directory's guidance, and the agent then appears to ignore
rules it was never shown. If you find one, recreate it as a symlink (`New-Item -ItemType SymbolicLink
-Path CLAUDE.md -Target AGENTS.md`). Never replace one with a real file and never put content in a
`CLAUDE.md`; all guidance goes in `AGENTS.md`.

Public-facing text — the README, the wiki, release notes — must state a rule itself rather than cite
these files.

**This file is loaded into every conversation, so it holds rules only.** Conventions, ownership
boundaries, workflow and the review standard live here. Explanations of how a mechanism works do
not, and adding them back is a regression in its own right - every line here is paid for on every
turn, whether or not it is relevant to the task.

Knowledge goes in one of two places instead, and both are updated in the same change as the code:

- **Mechanism detail belongs in a comment at the code it governs**, where whoever changes that line
  will actually see it. This is the default. A rule sitting hundreds of lines up in a document
  loaded at session start does not reliably connect to the line being changed.
- **Long-form device findings belong in `docs\`** - one topic file each, read on demand, indexed
  below. Put a finding there when it spans several files, records something that was DISPROVEN, or
  explains a decision that no single line of code owns.

## Contributor instructions (PRs)

**PRs that ignore the conventions and architecture in this file will be refused.** If you are an
agent preparing a contribution, treat everything below as binding, not advisory:

- **Follow the documented architecture.** Respect the module ownership boundaries (`Core\`,
  `Shell\`, `Overlay\`, `Input\`, `Settings\`, `Interop\` — put code in the narrowest applicable
  module), the NativeAOT constraints (no COM interop, `LibraryImport` with blittable signatures,
  source-generated JSON, no reflection-dependent packages), and the established idioms of the file
  you are editing.
- **Do not "fix" device-verified mechanisms.** Sections marked device-verified or live-verified
  (the Steam CEF integration, injected JS, boot/takeover sequencing, input handling) encode
  behavior that only reveals its constraints on real hardware or against a live Steam client.
  Changing them without re-verification — even when the change looks like an obvious cleanup or
  hardening — is grounds for refusal on its own.
- **Match the code conventions**: existing naming, comment density, XML docs on public production
  APIs, and the formatting gates (`./eng/verify.ps1` must pass — it runs Prettier over the whole
  repo including `.github\` and Markdown, plus C# lint/format, build, and tests).
- **Fill out the PR template honestly**, including what hardware the change was tested on;
  "compiles" is not "works" in this codebase.

## Working in this repository (maintainer workflow)

The section above governs contributions arriving as PRs. Work done *inside* this repository for the
maintainer follows a deliberately simpler flow — do not import the PR ceremony into it:

- **Commit directly to `master`.** Do not create a feature branch, and do not offer to — the
  maintainer says so when a branch is wanted. Release tags land on master regardless, so a branch
  only adds a merge step.
- **Committing and pushing are separate, and both are asked for explicitly.** "Commit it" means
  commit and stop. Never push, tag, or publish on your own initiative.
- **Know what automation actually runs.** Codex reviews **pull requests only** — a push to `master`
  gets no review from it. What a push does trigger is `.github\workflows\ci.yml` (it fires on both
  `push` and `pull_request`) and GitHub's **CodeQL**, which is configured through GitHub's *default
  setup* and therefore has **no workflow file in this tree** — do not go looking for `codeql.yml` or
  add one. CodeQL is a security scanner, not a code review; neither substitutes for the other.
- Version numbers stay user-owned — see the `<Version>` rule under Build and packaging.
- Every completed implementation task ends with `./build.ps1` and the installer copied to `Z:\`
  (see "Dev environment reality").

## What this is

WSGM ("Windows Steam Game Mode", formerly OpenFSE) reconstructs SteamOS Game Mode on Windows 11
gaming handhelds. **Explorer stays the registered Windows shell.** A SYSTEM logon service
(`src\WSGM.LogonService`, `WSGMLogonService`) launches WSGM's boot splash at sign-in to cover the
booting desktop; WSGM waits until Explorer finishes its logon prep (that one-per-session init is
what keeps touch features alive in game mode — device-verified), ends Explorer via its own orderly
Exit-Explorer path, and boots into Steam Big Picture with a controller/touch quick-access overlay. It
is **Steam-exclusive by design decision** — do not add multi-launcher support back; Steam is
auto-detected from the registry (`Core\Steam.cs`), never configured by path.

## Where the details live

`docs\` holds the device- and live-verified behaviour behind each subsystem: what was measured, on
what hardware, and which alternatives were tried and failed. **Read the relevant file before
changing anything it covers.** Sections marked device-verified or live-verified encode constraints
that only appear on real hardware or against a live Steam client; changing one without
re-verification is grounds for refusing a change on its own.

| Doc | Read before touching |
| --- | --- |
| `docs\boot-and-shell.md` | The logon service, `--boot` takeover, Explorer exit, boot splash, game/desktop transitions, tray host and taskbar |
| `docs\steam-input.md` | The Steam Input lease, the proxy DLL, hook installation, controller blocking |
| `docs\elevation.md` | Self-elevation, de-elevation, the scheduled-task mechanism, `WSGM.Launch` |
| `docs\steam-cef.md` | Anything driving Steam over its CEF port: library folders, injected tabs, the page badge, launch options, download-queue sorting |
| `docs\sd-cards.md` | Card formatting, card libraries, card identity and the card manager |
| `docs\overlay-and-input.md` | The quick-access panel, taskbar surfaces, SDL/gamepad ownership, touch and raw input |
| `docs\power-and-display.md` | Display profiles and HDR, mute-while-screen-off, keep-awake and wake-lock reporting |
| `docs\ui.md` | Themes, shared controls, Settings layout, the splash engine |
| `docs\decisions.md` | Standing decisions, accepted security posture, and approaches that are deliberately not taken |

Each module also has its own `AGENTS.md` (`src\WSGM\Core\`, `Shell\`, `Overlay\`, ...) carrying
the rules for that directory. Those load when you work in the directory, which is exactly when they
apply.

## Commands

```powershell
dotnet build src\WSGM\WSGM.csproj          # build (output is localized German: "0 Fehler" = success)
./eng/verify.ps1                             # Prettier + C# lint/format + Release build + unit tests + coverage
./eng/verify.ps1 -Fix                        # apply Prettier and C# lint/format fixes, then validate
.\build.ps1                                 # NativeAOT publish + Inno Setup installer → publish\WSGM-Setup-*.exe
                                            # (needs .NET 10 SDK, VS C++ build tools, Inno Setup 6)
src\WSGM\bin\...\WSGM.exe --settings        # safe to run locally: settings window only
src\WSGM\bin\...\WSGM.exe --overlay-test    # safe to run locally: overlay + activation surfaces, no apps started
```

## The Steam Input Lease library (`native\SteamInput`)

The Rust library that blocks Steam Input lives **in this repo** at `native\SteamInput` (Rust
workspace + C ABI + .NET binding). It is not a separate repository and WSGM is its only consumer, so
breaking its API is fine — change all layers together (Rust → `include\steam_input_lease.h` →
`bindings\SteamInterop.Net\` → `src\WSGM\SteamInterop\` → callers) and bump `sil_abi_version()`.

**It is built from source on every build.** `eng\build-steam-input-lease.ps1` compiles the workspace
and stages `steam_input_gate.dll`, `steam_input_lease_ffi.dll`, `steam-input-lease.exe`, and the two
license files into `src\WSGM\Native\SteamInputLease\`. `WSGM.csproj` copies the two DLLs and the
license files beside the AOT executable and the installer ships those; `steam-input-lease.exe` is
deliberately **not** shipped (see below). `build.ps1` calls it first; `eng\verify.ps1` calls it with
`-Validate`, which adds the library's own gates (`cargo clippy -- -D warnings`, `cargo test`). CI
therefore needs a Rust toolchain — it adds the clippy component and caches `target\`.

That staging directory is **generated and gitignored**; `native\SteamInput` is the tracked source.
Never hand-copy binaries into it. A Rust toolchain is now required to build WSGM at all.

`src\WSGM\SteamInterop\*.cs` are copies of `bindings\SteamInterop.Net\*.cs` **plus explicit
`using` directives** (neither WSGM nor `WSGM.Launch`, which links the same files, enables
`ImplicitUsings`) — diff, don't blind-copy. The Rust code
is deliberately not `cargo fmt` clean and has no fmt gate; do not reformat untouched code. Both
`native\SteamInput\` and the staging directory are in `.prettierignore` (the latter because
regenerating it would otherwise fail the next format check).

`steam-input-lease.exe` is a **development/diagnostic tool only** and is not installed. The
user-facing wrapper is `WSGM.Launch.exe`, which links the same binding mirror, takes the lease itself
via `--input-lease`, and carries the CLI's `--status`/`--rescan` diagnostics
(`docs\elevation.md`).

## The radio helper library (`native\Radio`)

Wi-Fi, Bluetooth, pairing, and touch-keyboard support live in this in-repo Rust workspace. WSGM's
NativeAOT executable has managed COM interop disabled, so `WSGM.Radio.dll` owns the WinRT and Win32
calls behind a flat C ABI. `eng\build-radio.ps1` builds it from source on every verification and
release build, staging `WSGM.Radio.dll` and the user-facing `WSGM.RadioProbe.exe` in
`src\WSGM\Native\Radio\`; that directory is generated and must never be hand-populated. The probe
is the device diagnostic for shell-less/elevated radio control and the Wi-Fi location-consent gate.

## Required build handoff

For every completed implementation task on this machine, always run `./build.ps1` before handing it
off. After a successful build, copy the freshly produced `publish\WSGM-Setup-*.exe` installer to
`Z:\`. Use PowerShell to select the newest matching installer and overwrite the matching artifact
on `Z:\`:

```powershell
$setup = Get-ChildItem -LiteralPath .\publish -Filter 'WSGM-Setup-*.exe' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $setup) { throw 'WSGM installer was not produced.' }
Copy-Item -LiteralPath $setup.FullName -Destination 'Z:\' -Force
```

Automated tests live in `tests\WSGM.Tests` and run through `dotnet test WSGM.slnx`. They cover
pure/stateful logic, source-generated config serialization, and isolated per-test HKCU snapshot
round trips. The CI workflow also collects Cobertura and LCOV coverage under `TestResults`. **Never
run `--shell` or `--boot` on a dev machine** — both end explorer and take over the session; never
run `WSGM.LogonService.exe --install` locally either. `--restore-shell` is the recovery path
(restores any legacy shell registration, disarms the service boot, starts explorer) and must stay
bulletproof (it runs before logging/Avalonia init).

All public production APIs require meaningful XML documentation (`CS1573`/`CS1591` stay enabled and
the Release verification build treats warnings as errors). Test method names are the executable
specification and are exempt from that API-documentation rule. Do not use coverage percentage as a
reason to automate the device-only flows listed below; add isolated unit tests around their pure
state/serialization/decision logic and retain the manual device-verification boundary.

The live shell, Steam protocol, device input, display-DPI, explorer, UAC, and lock-screen flows
require the safe manual modes (`--settings` and `--overlay-test`) plus the device-verification
process below; they must never be triggered by unattended tests.

## Dev environment reality

- **Get hands dirty before theorizing (hard lesson, user-mandated).** When a live Steam is reachable
  over the CEF port, PROTOTYPE AGAINST IT immediately — don't write long feasibility essays or hedge
  about fragility from the armchair. The injected library-tabs work looked "too fragile / needs the
  React module registry we don't have," until a few live `Runtime.evaluate` probes proved the
  registry (`webpackChunksteamui`), React, and a working tab injection in minutes. Reality is cheaper
  to query than to reason about: run the probe, inject the script, watch the screen. Estimate cost by
  doing, not by imagining. (`tools/WsgmLibTest/` — `cdp-eval.mjs raw`, `run-file.mjs <file>` — is the
  live probe harness; Steam BPM on the dev box is a CEF test rig even though WSGM itself never runs
  there.)
- **No controller hardware locally.** Real testing happens on a user's MSI Claw via pasted logs from
  `%LOCALAPPDATA%\WSGM\wsgm.log`. Every input/focus feature must log enough to be diagnosed remotely
  (`Gamepad added:`, `Controller input:`, `Gamepad nav:`, `Steam Input lease acquired/released`,
  `Explorer is running unelevated/ELEVATED`). Preserve and extend these lines; they are the only
  test harness.
- NativeAOT (`PublishAot=true`, `BuiltInComInteropSupport=false`): P/Invoke via `LibraryImport` with
  blittable types only, **no COM interop**, no reflection-dependent packages. `ppy.SDL3-CS` is used
  precisely because it is plain-DllImport. The Rust radio helper and the native volume helper own
  the WinRT/COM calls behind flat C ABIs. AOT may be dropped if ever truly necessary (user-approved),
  but so far never needed.

## Hard rules that are not negotiable

**Test-harness rule (hard):** no test or throwaway probe may touch `%LOCALAPPDATA%\WSGM` — a probe
once destroyed the developer's real `config.json`. Use temp dirs and the seams: `SplashAssets`/
`SplashTheme` take explicit target directories, `SettingsViewModel` has an internal ctor taking an
`AppConfig`. Never call `ConfigStore.Save/Load` or the parameterless `SettingsViewModel` ctor from a
test. `Log` is uninitialized in tests (writes are no-ops) — keep it that way.

- Version numbers are user-owned: never bump `<Version>` in `WSGM.csproj` on your OWN initiative
  (e.g. to disambiguate builds — use log content or timestamps for that). But a "tag the release"
  instruction IS the request to bump: set `<Version>` to that `vX.Y.Z` in the same commit BEFORE
  creating the tag, then tag. `release.yml` stamps the version from the tag on the CI runner only
  and never commits it back, so the tracked `<Version>` lags unless it is bumped here. `build.ps1`
  reads it and passes it to the installer, so that one line drives the local installer name and the
  app version.

# Implementation Architecture

Use this map when deciding where new code belongs. Keep direct dependencies narrow and communicate
across ownership boundaries through the named manager/coordinator; core/native layers must not depend
on Avalonia windows or controls.

| Area | Owns | May depend on | Must not own |
| --- | --- | --- | --- |
| `Program`, `App` | command-mode selection, recovery-first bootstrap, Avalonia lifetime | `Core`, `Shell`, `Settings` | feature policy or window behavior |
| `Core` | durable configuration, process/Steam/Explorer/elevation primitives, CEF bridge | `Interop`, BCL | Avalonia UI or session lifetime |
| `Shell` | game/desktop state machine and long-lived device/session managers | `Core`, `Interop`, `Overlay` facade | raw Win32 declarations or page-specific UI |
| `Overlay` | focused transient surfaces, focus restoration, activation handover | `Shell` coordinators, `Input`, `Controls` | Steam/Explorer transition implementation |
| `Input` | SDL ownership, gamepad navigation/chords, raw-input observation | SDL interop, Avalonia input primitives | global input interception or application policy |
| `Settings` | editing and committing user configuration | `Core`, shared controls/themes | live shell transition ownership |
| `Interop` | narrow Win32/native ABI boundary | BCL/native DLLs | application decisions or UI state |
| `Themes`, `Controls` | tokens, reusable presentation, AOT-safe commands | Avalonia | device/session/Steam policy |
| `WSGM.LogonService` | SYSTEM launch/watchdog boundary | shared boot manifest, Win32 | Avalonia or user-profile writes |
| `WSGM.Launch` | per-game wrapper: medium-integrity child lifetime, Steam Input lease | scheduled-task launcher, `SteamInterop` mirror | shell/session UI, launch-option writing |
| `native\*` | OS APIs unavailable to NativeAOT WSGM | Rust/C++ and C ABI | managed business logic |

## Application lifetime and state flow

1. `Program.Main` handles recovery and one-shot commands before logging or Avalonia. It selects the
   mode, performs shell-only elevation/mutex/crash-loop protection, and guarantees lease recovery on
   normal and fatal exit.
2. `App.OnFrameworkInitializationCompleted` loads the initial configuration and creates exactly one
   root: Settings/Welcome for safe UI modes, or a resident `ShellSession` for shell/overlay-test.
3. `ShellSession` creates `SteamMonitor`, `SessionModes`, and `OverlayController` once. It is the
   composition root for the running session; event subscriptions and disposable process resources
   must be rooted there for their required lifetime.
4. `SessionModes` serializes game/desktop transitions and emits state events. `OverlayController`
   requests transitions and owns presentation; it must not duplicate transition policy.
5. `ConfigStore` loads a replaceable `AppConfig`. `ShellSession` debounces file changes and calls
   `OverlayController.ApplyConfig`; controllers retain their own runtime state because reload swaps
   the configuration object.
6. Native helpers stay behind `Interop`. Managers translate their errors into logged, recoverable
   feature state; a missing helper must never take down the shell.

## Concurrency, UI, and resource ownership

- Avalonia controls, windows, focus, and observable view state are UI-thread owned. Perform blocking
  Explorer, process, file, CEF, and device calls off-thread, then marshal only the result back to the
  dispatcher.
- `async void` is allowed only for framework event handlers. Library/manager operations return
  `Task`/`Task<T>` and must observe failures; do not use `.Wait()` or `.Result`.
- Long-lived callbacks require a field-rooted owner and explicit `Dispose`/unsubscribe path. This is
  mandatory for file watchers, timers, raw-input windows, gamepad services, tray hosts, and native
  callback handles.
- Use one named synchronization gate for each shared workflow (for example config save, library-tab
  synchronization, or session transition). Do not introduce nested timeouts around `ConfigStore`.
- A best-effort recovery path may catch exceptions only when it preserves a usable desktop/session;
  log contextual failures everywhere normal diagnosis is possible. Never silently swallow a normal
  feature failure.

## Repository code conventions

- `.editorconfig` is authoritative: UTF-8, CRLF, final newline, trimmed trailing whitespace, and
  four-space indentation for C#/PowerShell-style code; AXAML, XML, project, JSON, and workflow files
  use two spaces. Run `eng\verify.ps1` rather than hand-applying formatters with different settings.
- C# is file-scoped with explicit `using` directives in WSGM. Keep system usings first, nullable
  annotations meaningful, braces present, and use `var` only when the right-hand side makes the type
  obvious.
- Public production types and members need meaningful XML documentation. Keep methods small around
  a single policy/side effect, make pure decision helpers `internal` where tests need them, and give
  tests descriptive executable-specification names.
- Prefer records/readonly value types for immutable data and sealed classes for stateful managers.
  Model finite state with enums and pure decision functions instead of scattered boolean combinations.
- Use `Log.Info/Warn/Error` with the operation and device-relevant state. Do not add Console output,
  reflection-based logging, or a second logging subsystem.
- Put Win32 constants, handles, ownership rules, and native marshalling at the `Interop` boundary.
  Callers should receive managed values/results, not raw pointers or unchecked handles.
- Do not add packages that require runtime reflection, managed COM interop, or JIT-only behavior.
  Pin UI package versions deliberately; NativeAOT publish is the compatibility proof.

# Agent Review Rules

Use this section for every requested code review, pull-request review, changeset audit, or review
comment. The objective is complete, evidence-backed defect discovery—not stylistic preference or a
quick scan of the diff.

## Required review scope

1. Read every changed file and every changed hunk, including generated-input definitions, project
   files, build scripts, installer changes, tests, native ABI layers, and documentation that changes
   an operational contract.
2. Trace each changed behavior from every relevant entry point through its complete affected code path:
   caller, state/config input, async/thread boundary, side effect, error/recovery path, cleanup, and
   user-visible outcome. Review both normal and failure paths.
3. Follow all changed contracts across module boundaries. In this repository that explicitly includes
   C# ↔ native ABI, config.json ↔ boot manifest ↔ SYSTEM service, shell ↔ overlay ↔ Steam, installer
   ↔ running-session recovery, and theme/control ↔ every consuming AXAML surface.
4. Compare the changes against the root and nearest nested `AGENTS.md` rules, existing tests, and the
   device-verified behaviour recorded in `docs\`. Treat a violation as a finding even when
   the changed code appears to work in isolation.
5. Review tests as production code: verify they exercise the changed contract, isolate machine state,
   detect the regression they claim to cover, and do not automate device-only or destructive flows.

**Refuse approval of any PR or changeset that violates the documented architecture or code
conventions.** Report every such violation as `blocker` severity, request correction, and do not
approve until the implementation conforms or the project instruction itself is deliberately updated.
For every refusal, give concise but concrete remediation: name the violated rule and exact location,
explain why the current structure does not fit, and prescribe the smallest compliant move, split, API
boundary, ownership change, or test adjustment needed to resolve it.

## Project-aware security and risk review

- Review against WSGM's actual trust model as recorded in `docs\decisions.md`, not generic
  least-privilege
  checklists. Elevated WSGM, elevated Steam, the SYSTEM logon service, scheduled-task de-elevation,
  native helper DLLs, Steam Input injection, registry/service management, and raw-input observation
  are deliberate product mechanisms. Their presence alone is never a finding.
- Flag an issue only when a change violates a stated boundary or introduces a concrete unsafe path—for
  example: a service launching an untrusted executable, an elevated Explorer, a broken token boundary,
  unchecked untrusted splash extraction, an ABI ownership error, or a recovery path that can strand a
  user without a desktop.
- Do not recommend removing elevation, avoiding native code/injection, adding consent dialogs,
  replacing scheduled tasks, or broadening sandboxing merely because those are conventional security
  defaults. Recommend an alternative only when it preserves the device-verified behavior and solves a
  demonstrated defect.
- Give operational correctness equal weight with security: regressions in Explorer recovery, Steam
  Input lease lifetime, touch/input behavior, Big Picture visibility, installer ordering, and remote
  device diagnosability are merge-blocking even when no conventional security category applies.

## Finding standard

- Report **every issue found**, regardless of severity. Do not omit a valid low-severity correctness,
  reliability, security, performance, compatibility, maintainability, or test-coverage defect merely
  because a more serious issue exists.
- Report only actionable defects with a concrete failure mode. Do not file nitpicks about formatting,
  naming, personal taste, hypothetical abstractions, or pre-existing unrelated code unless the
  changeset makes the problem materially worse.
- Each finding must state: severity, precise file and line, the triggering condition, evidence from
  the affected path, concrete impact, and the smallest safe correction. A review comment must stand
  alone without requiring the author to rediscover the reasoning.
- Severity communicates impact, not whether the finding is reported: `blocker` prevents safe merge or
  recovery; `high` risks data/session/security breakage; `medium` causes a real incorrect or degraded
  behavior; `low` is a bounded but demonstrable defect. Never inflate severity to win an argument.
- If evidence is insufficient, investigate the path or label it as a question/risk—not as a defect.
  Never invent runtime behavior, claim a test was run when it was not, or treat speculation as proof.

## Review output and closure

- Order findings by severity, then affected execution path. Lead with findings; put summaries after
  them. Include file/line anchors whenever the review surface supports them.
- If no defects are found, state `No findings` and list the code paths and failure modes actually
  reviewed, plus any residual device-only validation that could not be performed. Do not imply that
  unreviewed code is approved.
- Re-review changed fixes and all paths they affect. A finding is closed only when the correction,
  regression coverage, and relevant recovery/cleanup behavior have been checked.
- Keep review feedback separate from implementation changes unless the user explicitly requests the
  fixes. A review reports evidence first; it does not silently mutate the reviewed code.

# Shared Engineering Rules

Generic rules for the languages and tooling in this repo. They apply only where the WSGM-specific
guidance above does not say otherwise — on any conflict, the project sections win. Known overrides:
WSGM's `Log` is the only logging subsystem (no Serilog or Console output); the Rust code deliberately
has no `cargo fmt` gate; device-only flows are never automated no matter what coverage goals say;
version numbers are user-owned.

## Coding conventions

- `PascalCase` for public types and members, `camelCase` for parameters and locals, `_camelCase` for
  private fields — except where a language's own conventions differ (Rust `snake_case`).
- Write clear, descriptive commit messages in English.
- `.editorconfig` is authoritative for formatting; run `eng\verify.ps1` rather than hand-applying
  formatters, and never override its line endings or indentation with a generic convention.

## Design and error handling

- Classes for stateful services and domain entities; pure functions for stateless transformations,
  validation, and decision logic. Prefer immutable data (records, readonly structs) between
  components; mutate state only inside well-encapsulated owners.
- Constructor injection for wiring services; interfaces over concrete hierarchies where behavior
  varies; model finite state with enums and pure decision functions, not boolean combinations.
- Expected failures are values (Result/Option-style returns, meaningful nullability); exceptions are
  for truly exceptional conditions. Wrap third-party errors at module boundaries into
  domain-meaningful, logged, recoverable feature state.
- Never swallow exceptions silently — log with the operation and relevant state. In retry logic,
  retry only transient failures, with backoff and a maximum count. Document at API boundaries which
  errors a function can produce and what callers should do about them.

## Performance

- Profile and measure before optimizing; optimize the critical path first; benchmark before and
  after every optimization.
- Debounce user-input-driven operations; run I/O-bound work async/off-thread; cache expensive
  computations with a deliberate invalidation story.

## Testing

- Unit-test new logic immediately; one behavior per test; Arrange-Act-Assert; fast, isolated,
  deterministic; mock external dependencies through seams; cover edge cases and error paths, not
  just the happy path.
- When a bug is found, write the failing test that reproduces it first, then fix.
- Tests stay independent of each other and of machine state. Run the full suite (via
  `eng\verify.ps1`) before committing; never merge with failing tests, and fix flaky tests instead
  of exempting them.
- xUnit: `[Fact]` for single cases, `[Theory]` + `[InlineData]`/`[MemberData]` for parameterized;
  test names follow `MethodName_Scenario_ExpectedBehavior` and are the executable specification.

## C# / NativeAOT

- Use C# 12+ features (primary constructors, collection expressions, pattern matching), file-scoped
  namespaces, and meaningful nullable annotations. `var` only when the right-hand side makes the
  type obvious.
- `async`/`await` for all I/O — never `.Result`/`.Wait()`. Omit the `Async` suffix unless both
  variants exist. Prefix interfaces with `I`. Prefer LINQ for querying collections and
  `Span<T>`/`Memory<T>` where allocation-free buffers matter.
- NativeAOT discipline: source generators instead of reflection (`[JsonSerializable]` for JSON), no
  `dynamic`, no `Assembly.Load`, annotate unavoidable reflection with
  `DynamicallyAccessedMembersAttribute`. The AOT publish (`build.ps1`) is the compatibility proof
  for every dependency choice.

## Rust (`native\*`)

- Prefer borrowing (`&T`, `&str`, slices) over cloning; `Result<T, E>` + `?` for fallible
  operations and `Option<T>` for absence; typed errors (`thiserror`-style); no `.unwrap()` in
  library code — `.expect("reason")` only for true invariants; never panic across the C ABI.
- Iterators and combinators over manual loops; `#[derive]` the common traits; `#[must_use]` where
  discarding a result is a bug; document public items with `///` including `# Errors`/`# Safety`
  where applicable.
- The gates are `cargo clippy -- -D warnings` and `cargo test` (run via the `eng\build-*.ps1`
  scripts with `-Validate`). There is deliberately no fmt gate — do not reformat untouched code.
- Unit tests inline under `#[cfg(test)]`; integration tests in `tests/`.

## PowerShell (`eng\`, build scripts)

- Approved Verb-Noun names, `[CmdletBinding()]`, typed `param()` blocks with validation attributes;
  full cmdlet names, no aliases; `Join-Path` for path construction.
- `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'` at the top; typed
  `try`/`catch`, never an empty `catch {}`; `Write-Error -ErrorRecord $_` to re-throw after logging.
- `[System.Collections.Generic.List[object]]` + `.Add()` instead of array `+=`; batch filesystem
  queries instead of per-item `Test-Path` loops; avoid `Invoke-Expression`.

## GitHub Actions

- Every job gets `timeout-minutes`; every step gets a `name:`; workflow-level `permissions:` kept
  minimal (default read-only, widen per job only as needed).
- Cache dependencies keyed on lockfile hashes with `restore-keys` fallback; prefer the setup
  actions' built-in caching; never cache ephemeral outputs (logs, test reports).
- Concurrency groups with `cancel-in-progress` for PR workflows; path filters to skip irrelevant
  runs; parallelize independent jobs and chain the rest with `needs:`.
- Keep third-party actions pinned and updated (Dependabot); never combine `pull_request_target`
  with a checkout of the PR head.

## Security

- Validate untrusted input with explicit bounds, size caps, and decode budgets — the splash-theme
  defense set in `docs\ui.md` is the model.
- Never hardcode secrets; never concatenate untrusted input into command lines, queries, or
  injected script text (JSON-encode, as the CEF bridge does).
- Judge findings against WSGM's accepted security posture and trust model
  (see `docs\decisions.md` and Agent Review Rules) before proposing a fix; treat high-severity dependency CVEs as release blockers.

## Collaboration

- Keep changesets cohesive; state their rationale and test plan; keep formatting-only work separate
  from behavior changes when practical. Reviews follow the Agent Review Rules above.
- Decompose features into small, independently verifiable slices; separate research spikes from
  implementation; record known debt as follow-up tasks instead of expanding scope.
- Keep this file current (see the header) and prune stale guidance — outdated instructions are
  worse than none.
