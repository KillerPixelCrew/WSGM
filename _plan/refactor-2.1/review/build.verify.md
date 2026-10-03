# BUILD adversarial verification

Verifier for `_plan/refactor-2.1/review/build.md`. Baseline `master` 1329813f, read-only. Every medium-or-higher finding
and every plan claim marked inaccurate/partial/over-engineered was re-read against the cited code and its
callers. Additionally read in full: `eng/verify.ps1`, `build.ps1`, `eng/build-bundle.ps1`, `eng/package-plugin.ps1`,
`eng/plugin-package-common.ps1`, `eng/pack-device.ps1`, `eng/publish-device-lab.ps1`, `eng/new-plugin.ps1`,
`eng/plugin-manifest.cs`, `eng/build-steam-assets.mjs`, `eng/build-uwp-bridge.ps1`, `eng/assert-controller-pin.ps1`,
`eng/acquire-controller-dependencies.ps1`, `eng/check-no-live-data-paths.ps1`, `eng/check-version-sync.ps1`,
`eng/stamp-version.ps1`, `eng/wsgm-revision.targets`, the swap half of `eng/dev-deploy.ps1`, `tests/Shared/*`,
`DeviceBoundaryTests`, `ContractBoundaryTests`, `DeviceLabScaffoldingTests` (build part), `WindowsPowerTests`,
`ModernStandbyTests`, `PowerRequestTests`, the `ConfigurationTests` mutex tests, every first-party csproj
reference/linked-compile list, `.editorconfig`, `.gitattributes`, `.prettierignore`, `.github/dependabot.yml`,
`ci.yml`, `release.yml`.

## Refuted

None of the medium-or-higher findings is refuted outright: each cited behaviour exists in the code. Several are
over-rated or carry a wrong detail; those are under Corrected.

## Corrected

- **BUILD-001 (high -> medium).** The gate does run both suites (`verify.ps1:161-173` enumerates the slnx
  `/tests/` folder, which includes the WDC test project). But neither hazard fires on today's code:
  `CancelledActionsNeverDispatch` (`WindowsPowerTests.cs:55-62`) dispatches only after a cancellation-admission
  regression (Codex rated it Medium, A01-F002), and the three `Local\WSGM.Config` probes
  (`ConfigurationTests.cs:621,724,756`) contend with a running WSGM for at most 200-2000 ms, producing flaky
  failures and a briefly blocked config save, not data loss. The prerequisite list is also incomplete:
  A01-F006 (`DisplayTopologyTests` calls the public real topology wait, hardware-dependent on a regression) is
  the same class and is "NOT READY" in W02. R1 should name W02_02, A01-F006's seam and the config-mutex fix.
- **BUILD-002 (medium -> low).** Behaviour confirmed: `jb cleanupcode` rewrites in place in both modes
  (`verify.ps1:131-133`) and `git diff --exit-code -- src tests` (`:134-136`) fails on any unstaged edit,
  including non-C# files under `src` (csproj, manifest, axaml). It is workflow friction on an uncommitted tree,
  not a defect in the committed-head gate CLAUDE.md prescribes for PRs. The "contradicts eng/AGENTS.md:17"
  claim is weak: that line does not promise the check mode leaves files alone. See BUILD-V-003 for the one
  concrete failure it causes.
- **BUILD-004 (medium, recommendation scope).** Confirmed and stronger than stated: Dependabot is active and
  merged (`20c940c4`, PR #186) is the bump that put Device Lab on Avalonia 12.1.3 while every other project is
  on 12.1.2. But requirement 12 freezes Avalonia *for this refactor*; a permanent `ignore` in
  `.github/dependabot.yml` changes the maintainer's standing dependency policy. Present it as a maintainer
  decision (permanent ignore, or simply not merging Avalonia PRs until 2.1.0 ships), not as a silent B1 edit.
- **BUILD-006 (medium -> low).** A HidHide bump fails loudly: `Copy-Item` at `build.ps1:158-161` throws under
  `$ErrorActionPreference = "Stop"`, and a usbip bump is already caught at verify time by
  `assert-controller-pin.ps1:104-108`. Nothing wrong can ship; the cost is a late failure. Setup itself is not a
  third copy (`SetupEngine.cs:868` globs `HidHide*.exe`).
- **BUILD-013 (wording).** The regex slices do not "keep passing against dead text" on a rename:
  `steam.match(...)[1]` (`check-steam-module-discovery.mjs:14-23,36-44`) throws a TypeError when the method is
  gone. Silent pass only happens if the old method survives beside a new launch path. The finding stands; the
  failure mode is loud in the common case.
- **BUILD-014 (scope).** Confirmed (4096 in `package-plugin.ps1:52` vs 1024/512/128 MiB/512 MiB duplicated in
  `PluginPackageFile.cs:31-34` and `PluginPackageWorkflow.cs:44-47`; host-provided names in
  `PluginPackageLoader.cs:172-179` and `plugin-package-common.ps1:80-81`). Two additions: the packer's native-image
  refusal (`package-plugin.ps1:55-62`) is a third copy of `PluginPackageFile.IsManagedImage`
  (`PluginPackageFile.cs:278-281,365-386`) and must move with the rest; and whichever owner keeps the package
  caps must justify them against the no-arbitrary-limits rule (they reject rather than truncate, so they are
  admissible as zip-bomb bounds, but the plan should say so instead of just unifying the numbers).
- **BUILD-018 (recommendation).** The risk is real (no build or test compiles the two here-strings in
  `new-plugin.ps1:81-166`). The proposed fix does not build green as written: compiling the common template into
  `WSGM.Plugin.Sdk.Tests` hits `csharp_prefer_braces = true:warning` (`.editorconfig:19`) with
  `EnforceCodeStyleInBuild` and `verify.ps1:155 --warnaserror` on the braceless `if` at `new-plugin.ps1:114-115`,
  and `dotnet format WSGM.slnx style --verify-no-changes` (`verify.ps1:143-153`) also reports the one-line bodies.
  The templates must be restyled to repository layout in the same batch (the generated text changes, harmlessly),
  and `.gitattributes` has no `eol=crlf` rule for `eng/**/*.cs`.
- **BUILD-023 (scope).** The duplicate set is larger than two types: WSGM and WSGM.Launch both compile the
  Steam Input binding (`WSGM.csproj:45-48`, `WSGM.Launch.csproj`), whose types are *public*
  (`SteamInputClientOptions`, `SteamInputStatus`, ... in `bindings/SteamInterop.Net/Models.cs`), so any test using
  them is CS0433-ambiguous even without `InternalsVisibleTo`. PackagedLaunch is not part of it (it deliberately
  grants no IVT, `WSGM.PackagedLaunch.csproj:46`). An `Aliases="launch"` reference also needs `extern alias` in the
  five test files that use Launch types (`LaunchWrapperTests`, `SuspendedProcessTests`, `UnelevatedLauncherTests`,
  `LaunchWrapperCommandTests`, `RunningApplicationTargetTests`); still a nit.
- **BUILD-029 / B5 (attribution).** `DeviceSdkHasNoProjectOrPackageDependencies` lives in
  `tests/WSGM.Tests/Boundaries/DeviceBoundaryTests.cs:61-71`, not in `ContractBoundaryTests`. `ContractBoundaryTests`
  has no dependency assertion to "move out"; B5 touches only `DeviceBoundaryTests.cs`.
- **C17 (detail).** `tests/Shared/PluginManifestFixture.cs:24` does not pin API 11; it uses `DeviceApi.Version` and
  follows any bump. The literal pin is `ContractBoundaryTests.cs:29` (not `:36`).
- **BUILD-031 / C19 (citation).** The symlink item has a ledger id, U04A-LFA-027 ("accepted build simplification;
  H01"), which the review did not cite. The review's caution (prove `Link` keeps the avares paths) is right; the
  ledger's "accepted" disposition is unproven.
- **BUILD-034 (one bullet).** `docs/logging.md:10,28` is accurate today (`PluginTrace` exists, e.g.
  `DevicePluginRuntime.cs`, the Ally package); it becomes stale only when F02 lands. It belongs to F02's doc diff,
  not to "already wrong".
- **BUILD-005 (recommendation).** Finding confirmed, but the "glob every `*.txt`/`*.md` in `publish\App`" fix changes
  the payload: `publish\App` also holds `VIIPER-LICENSE.txt` and `VIIPER-NOTICE.md`, which `build.ps1:158-160`
  deliberately routes to `Payload\Controller`. Add the two missing names to the list (or glob only the
  `Licenses\*.txt` sources) instead.

## Confirmed

BUILD-003, BUILD-005, BUILD-007, BUILD-008, BUILD-009, BUILD-010, BUILD-011, BUILD-012, BUILD-015, BUILD-016,
BUILD-017, BUILD-019, BUILD-020, BUILD-021, BUILD-022, BUILD-024, BUILD-025, BUILD-026, BUILD-027, BUILD-028,
BUILD-030, BUILD-032, BUILD-033, BUILD-035, BUILD-036.
Plan claims: C1, C2, C3 (with the BUILD-001 correction), C4, C6, C7, C8, C9, C10, C11, C12, C14, C15, C16, C18,
C22, C23, C24, C26 (A02_01 step 5's GUID fixture root with containment checks is the same ceremony), C27.

## Missed findings

**BUILD-V-001 (low) The asset builder writes into the embedded-resource directory even in check mode.**
`eng/build-steam-assets.mjs:201-217` writes `NativeQamBootstrap.generated.js` into
`src/WSGM/Core/SteamUiAssets` on every run, `--check` included, then deletes it. That directory is an
`EmbeddedResource` glob (`WSGM.csproj` `Core\SteamUiAssets\*.js`), and the check itself fails when any second `.js`
is present (`:244-252`). A `dotnet build` running at the same moment embeds the stray file, and a killed node
process leaves it behind so the next gate fails with "explicit, reviewed set". Recommendation: feed the text to
Prettier on stdin with `--stdin-filepath <outputPath>` (spawnSync `input`), which keeps the `.prettierrc`
override for that path and writes nothing in check mode. Removes a write; no new mechanism.

**BUILD-V-002 (low) The canonical gate is not safe for in-progress source, only for a committed head, and plans
tell workers to run it mid-implementation.** `verify.ps1:134-136` diffs all of `src`/`tests` (not only `*.cs`)
against the index after an in-place `jb cleanupcode`. Every batch in this plan validates in a shared working tree
with uncommitted edits. Consequence for the refactor: any batch that runs `eng/verify.ps1` before committing
fails or, with `-Fix`, mixes Rider's rewrites into the batch diff. Recommendation: the plan states that batches use
the narrow filters plus `dotnet build`/`dotnet format --verify-no-changes`, and `eng/verify.ps1` runs only on a
committed head (B11), unless BUILD-002's before/after comparison lands first. (Extends BUILD-002; kept separate
because it is a plan-procedure gap, not a script bug.)

**BUILD-V-003 (nit) The release workflow's version stamp trips the cleanup diff.**
`release.yml` runs `eng/stamp-version.ps1` (rewrites `src/WSGM/WSGM.csproj` and `src/WSGM/app.manifest`) and then
`eng/verify.ps1`, whose `git diff --exit-code -- src tests` (`verify.ps1:135`) covers those files. Any tag whose
version differs from the committed csproj (a `-rc` tag, or a tag pushed before the csproj bump) fails the release
job at verify. Recommendation: BUILD-002's before/after comparison fixes it; otherwise scope the diff to `*.cs`.

**BUILD-V-004 (nit) Stale agent worktree nested in the checkout.** `git worktree list` shows
`D:/Coding/WSGM/.claude/worktrees/agent-a1a3754e7bef61c5c` (commit 8fa7510e, still on the USBip 0.9.8.0 pins in its
`build.ps1`/`assert-component-staging.ps1`/lock) plus twelve other registered worktrees. Git, Prettier
(`.prettierignore` `.claude/`) and the guidance check exclude it, but repository-wide `rg --hidden --no-ignore` and
glob sweeps return its stale copies, which is exactly the "enumerate every caller" step this refactor relies on.
The current audit inventories do not include it (checked `source-inventory.json`, `consumer-references.json`).
Recommendation: the plan's caller inventories exclude `.claude/` explicitly; removing stale worktrees is the
maintainer's call (destructive), not an agent step.

**BUILD-V-005 (low) The verify tools step as proposed rebuilds WSGM for a second runtime graph.**
`tools/DeckSpike/DeckSpike.csproj` is `win-x64` self-contained and references `src/WSGM/WSGM.csproj`;
`tools/OverlayPreview` is `win-x64` self-contained and references the self-contained `WSGM.UiTests`. Building them
in verify (BUILD-003) restores and compiles WSGM and its references again for the RID, not reusing the solution
build, and PerfLab/HcDeviceExtract pull Microsoft.Windows.EventTracing and Roslyn. Recommendation: decide Q2 first
(deleting DeckSpike and SteamReceiver removes the costliest and the `InternalsVisibleTo`), then build the
survivors with `--no-restore` after one restore that includes them, or add them to a `/tools/` slnx folder that the
jb/`dotnet format` steps skip.

## Batch problems

- **BUILD-B1** fails its own test. The two proposed live-data patterns match existing comment lines in scanned
  directories: `tests/WSGM.Tests/Core/SteamInputShimTests.cs:12`, `tests/WSGM.Tests/Settings/SettingsViewModelSplashTests.cs:9,174`,
  `src/WSGM.DeviceLab/Capture/Live/LabWmiFirmwareEvents.cs:279` (`%LOCALAPPDATA%\WSGM` in prose), so
  `check-no-live-data-paths.ps1` throws after the change. B1 must reword those comments or carry allow markers, and
  list those files. The Dependabot ignore needs a maintainer decision (see BUILD-004), not a silent edit.
- **BUILD-B2** "notices by glob" would copy VIIPER notices into `Payload\App` (see BUILD-005 correction); add the
  two names instead. `Copy-RuntimeNotices` must take the project's `obj\project.assets.json` path as a parameter
  (today it hard-codes Device Lab's at `device-lab-publish.ps1:95`), and WSGM's restore must be the RID restore
  `build.ps1:72` already does.
- **BUILD-B4** does not build green under `--warnaserror`/`dotnet format` unless the templates are restyled
  (BUILD-018 correction). Also both templates compiled into one assembly need distinct namespaces, which changes the
  generated `entryType` in `new-plugin.ps1:41`; the script and `docs/plugin-system.md:223-250` change with it.
- **BUILD-B5** cites a dependency assertion in `ContractBoundaryTests` that does not exist; only
  `DeviceBoundaryTests.cs` is replaced.
- **BUILD-B6** moves files that other domains change or delete: plan line 107 turns Launch's scheduled-task XML into a
  WSGM-side COM adapter, and session.md reshapes the launcher/logon pieces. Moving `ScheduledTaskXml.cs` and similar
  files to `src/Shared` before those land is churn on code a later batch rewrites (greenfield rule). Order B6 after
  the session/winsvc batches and move only files whose sharing survives them; the four interop files still wait
  for Q1. The `.editorconfig` blocks at `:139-148` must follow the files.
- **BUILD-B7** "remove the PS-side PE/entry checks" silently drops the packer's native-image refusal unless
  `validate-package` carries it (runtime still refuses, but only after install). Make it an explicit step.
- **BUILD-B8** bundles independent work behind four other domains: the tools compile step (BUILD-003) and the dead
  plugin discovery/cap removal (BUILD-015) depend on nothing and can land in B1/B3; only the scraper replacements
  need session/steamhost/toolkitjs/T01. As written, the cheap fixes wait for the whole refactor.
- **BUILD-B9** is fine under the simplify rule but changes two guidance files (`eng/AGENTS.md:40-42`,
  `src/WSGM.DeviceLab/AGENTS.md:160`); it is blocked on Q3 and must ship the guidance diff for sign-off.
- **R1 / BUILD-B11** prerequisite list should add A01-F006 (WDC topology wait seam), which is NOT READY in W02;
  otherwise the "safe" final gate still runs a hardware-dependent WDC test.
- **R2** (replace I01/I02 with per-batch child-then-gitlink) is consistent with CLAUDE.md and keeps the parent
  building, but must keep requirement 16's independent child validation explicit in each library batch
  (child-local build/tests on both WDC frameworks) rather than dropping it with I02.
