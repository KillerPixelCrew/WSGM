# BUILD review: project graph, layering, shared code, eng/build, tools, test infrastructure, docs

Reviewer domain: cross-cutting structure. Baseline: `master` 1329813f, clean tree. Read-only review; no build, test,
git mutation, Steam/CEF, hardware or live-machine action was performed.

Read in full: `WSGM.slnx`, `WSGM.slnx.DotSettings`, `Directory.Build.props`, `global.json`, `.editorconfig`,
`.gitattributes`, `.gitmodules`, `.prettierrc.json`, `.prettierignore`, `package.json`, `coverlet.runsettings`,
`.config/dotnet-tools.json`, all 30 tracked first-party `*.csproj` (17 src, 13 tests) plus the 6 tool projects and the
WDC/toolkit library and test project files; every `eng/**` script (32) and `eng/wsgm-revision.targets`; `build.ps1`;
`.github/**` (workflows, dependabot, templates); `plugins/curated/*.json`; `tests/Shared/*` (5); tools `OverlayPreview`,
`LiveBackdropSample`, all tool READMEs, `PerfLab/perf-capture.ps1`, `WsgmLibTest/{cdp,cdp-eval,run-file,run-file-target,
run-prod-sort,art-test}.mjs` and the head/scraping parts of `qam-harness.mjs`, `DeckSpike/Program.cs` (dependency surface),
`HcDeviceExtract/Program.cs` (entry/IO); root, `eng`, `src/WSGM`, `tests/WSGM.Tests`, `tests/WSGM.UiTests` AGENTS.md;
`docs/README.md`, `docs/logging.md`, `docs/ui.md` test section. Traced across the boundary: `PluginPackageLoader.cs`
(`HostOwned`), `PluginPackageFile.cs` limits, `PluginPackageWorkflow.cs` limits, `DeviceBoundaryTests`,
`ContractBoundaryTests`, `DevicePackageOutputTests`, `DeviceLabScaffoldingTests`, `ConfigurationTests` mutex lines,
`UnelevatedLauncherTests`, every linked-source consumer. Docs were checked mechanically (every repository path named in
docs/READMEs/AGENTS resolves; API-version and dependency claims grepped against code) and by sampling; not every sentence
of the ~7,000 lines of docs was verified.

Prior coverage: the ENG/DOC/XCR/XCB audit units (U21A, U21B, U22A, U22B, U23, U24) were never started
(`audit-coverage.md:53-58`). The only prior findings in this domain are A02-F020, A02-F021, A02-F022, A02-F002, U04A-LFA-022,
A01-F002 and the guidance nits U01-068/U02B-SUTC-024/PV08-011. Everything else below is NEW unless cited. Where another
Claude domain report already owns a fix, it is cited by its id (sdk.md, toolkitjs.md, steamhost.md, session.md,
input.md, gpuir.md, packages.md, library.md).

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | "`WSGM.slnx` is authoritative: 17 first-party production projects, 13 first-party test projects, two library projects and two library test projects" (refactor-plan.md:15) | accurate | `WSGM.slnx:2-37` | none |
| C2 | "Tools outside the solution remain in scope where first-party" (refactor-plan.md:15) | accurate, incomplete | 6 tool projects exist outside the solution; none is compiled by `eng/verify.ps1` or CI (`ci.yml:70-72`) | Add a compile step for tools (BUILD-003) or nothing guarantees "no parent compile gap" for them (api-integration.md "Final source/payload coherence"). |
| C3 | "The unsafe real-power and production-config-mutex tests ... were deliberately outside these filters" (refactor-plan.md:17); "Fix unsafe suite dependencies before any broad test/gate admission" (refactor-plan.md:173) | accurate; consequence understated | `WindowsPowerTests.cs:55-62` (A01-F002); `ConfigurationTests.cs:621,724,756` (U04A-LFA-022). `eng/verify.ps1:161-173` runs every `/tests/` project in the slnx, which includes the WDC suite, so the canonical gate itself is unsafe on the dev machine today. | State explicitly: `eng/verify.ps1` and CI may not be run until W02_02 and the config mutex fix land (BUILD-001). |
| C4 | "Keep every existing Avalonia package version and framework dependency, including current differences between projects" (refactor-plan.md:29, api-integration.md frozen comparison) | accurate; one open door | Versions: WSGM/Setup/LiveBackdrop/UiTests/LiveBackdropSample 12.1.2, DeviceLab 12.1.3 (`WSGM.DeviceLab.csproj:17-20`), Labs.Panels 12.0.2, FluentAvaloniaUI 3.1.0, ColorPicker 12.1.2, Headless/Skia 12.1.2. `.github/dependabot.yml:5-13` groups every NuGet minor/patch update for `/src/*` and `/tests/*` with no Avalonia ignore. | Add a Dependabot ignore for `Avalonia*` and `FluentAvaloniaUI` (BUILD-004); list all nine references in the frozen comparison. |
| C5 | "Source already says `2.1.0`" (refactor-plan.md:31) | accurate | `WSGM.csproj:13`; `wsgm-revision.targets:3` comment still says 2.0.0 (BUILD-030) | fix the comment |
| C6 | "`Plugin.Sdk` really references Device.Sdk and SteamUiToolkit, contrary to its README's zero-dependency claim ... intentional ... documented accurately" (refactor-plan.md:48) | accurate | `WSGM.Plugin.Sdk.csproj:19,21`; `src/WSGM.Plugin.Sdk/README.md:13` "It depends on nothing". A02-F002 / A02_DOC, sdk.md C3. | none |
| C7 | WDC: "Own child build/style configuration and multi-target tests for .NET 8 and 10" (refactor-plan.md:129) | accurate | WDC has no `Directory.Build.props` or `.editorconfig`, so inside this checkout it inherits WSGM's `Directory.Build.props` (`EnforceCodeStyleInBuild`, `NuGetAudit=false`, `AVLN3001`) and WSGM's `root = true` `.editorconfig`; standalone it builds with neither. Test project is `net8.0-windows10.0.19041.0` only. | Parent side: once WDC owns its roots, `verify.ps1`'s `--warnaserror` build stops imposing WSGM analyzers on WDC; note it so a new warning set is expected (BUILD-012). |
| C8 | "The final automated evidence includes both WDC frameworks, the toolkit's complete .NET/Node suites ... and `eng/verify.ps1`" (refactor-plan.md:173) | partially | `verify.ps1` runs one TFM of WDC (test project single-target) and only 5 of 16 toolkit Node checks (`package.json:8`; toolkitjs.md C20/TOOLKITJS-009). | Both close only after the W-domain multi-target and toolkitjs B1 changes; `dotnet test <csproj>` then runs both TFMs without a verify change. |
| C9 | "Do not lower coverage thresholds" (refactor-plan.md:173) | inaccurate premise | No threshold exists: `coverlet.runsettings` has none, `verify.ps1:167-170` only collects. Coverage is collected for `WSGM.Tests` only. | Drop the phrase; keep "coverage is a gap finder" (refactor-plan.md:177). Do not add a threshold (no-arbitrary-limits). |
| C10 | "Before any commit, run Prettier ... Rider Full Cleanup is the C# authority ... Parent-owned checks must not format child trees" (refactor-plan.md:175) | partially | `verify.ps1:131-132,149` exclude `external/` from jb and `dotnet format`; but `verify.ps1:155` `--warnaserror` build still enforces WSGM's inherited style analyzers on WDC (C7). Also `verify.ps1:131` rewrites files even without `-Fix` (BUILD-002). | as BUILD-002/012 |
| C11 | "Shared linked native declarations remain one authored source; do not add GPL product policy to MIT SDK/library helpers" (refactor-plan.md:157) | partially | One authored source is true, but 14 linked files live inside owner projects (`src/WSGM/Core`, `src/WSGM/Interop`, `src/WSGM.Launch`, `src/WSGM.Install`), not in a shared home; MIT Device Lab compiles 4 GPL-located files (A02-F022). | Move to `src/Shared/<area>` (BUILD-019). |
| C12 | "Build scripts keep deterministic owned staging and existing notices/pins" (refactor-plan.md:157) | inaccurate premise for notices | `build.ps1:143-148` payload allowlist omits `Microsoft.Data.Sqlite-MIT.txt` and `SQLitePCLRaw-Apache-2.0.txt` although their DLLs ship (`build.ps1:155-157`), and the self-contained app ships no .NET runtime notices while Device Lab does (`device-lab-publish.ps1:95-146`). | Fix before "keeping" (BUILD-005). |
| C13 | "Use the current shared `master` checkout; no worktree, task branch, clone or PR. Source writes, format/cleanup, builds/tests and commits are serialized" (refactor-plan.md:161) | accurate | CLAUDE.md "How the maintainer works" | none |
| C14 | WDC/toolkit "Parent may be nonbuilding until I01 adapts every inventoried caller" (api-integration.md, groups WDC 0.2.0 / Toolkit 0.2.0) | over-engineered and risky | The parent references both libraries by `ProjectReference` into the submodule working tree (`WSGM.csproj` WDC/toolkit refs, `WSGM.Plugin.NvidiaGpu.csproj:12`, `WSGM.Plugin.Sdk.csproj:19`), so every child edit breaks the parent build immediately. | Each library API batch carries its consumer adaptation in the same working-tree change, child commit and push first, then the parent gitlink commit. Parent green after every batch (refinement R2). |
| C15 | I01 interim + I02 final child publication with "remote tree equality" (planning-corrections-r2.md R2-3, api-integration.md) | over-engineered | CLAUDE.md already fixes the order: "Commit and push a submodule child before recording its gitlink". | Replace I01/I02 with that per-batch rule; no separate publication phases or tree-equality step (refinement R2). |
| C16 | "Advance WDC to 0.2.0 ... toolkit package to 0.2.0 ... Device 12 / SDK package 0.5.0 and common Plugin 4 / SDK package 0.3.0" (refactor-plan.md:129,145,149) | accurate as numbers; one gap | `DeviceApi.cs:76` = 11, `PluginManifest.cs:16` = 3, Device SDK 0.4.0, Plugin SDK 0.2.0. `build-bundle.ps1:115` packs both SDKs with `-p:Version=$wsgmVersion` (2.1.0) for the community feed, so the SDK package version a plugin author would pin never appears in the bundle build. | Note the two version identities (BUILD-027). |
| C17 | "manifests, templates, fixtures and all consumers update coherently" (refactor-plan.md:149) | partially | `eng/new-plugin.ps1:81-166` holds two C# templates as here-strings that no build or test compiles; `tests/Shared/PluginManifestFixture.cs:24`, `ContractBoundaryTests.cs:36` pin API 11. | Make the templates compiled sources (BUILD-018) so the API change cannot leave them broken. |
| C18 | "Replace slice/regex-derived test entry points with explicit emitted fragment markers/manifest" (refactor-plan.md:145) | accurate for the toolkit; misses WSGM's own scrapers | `eng/check-steam-module-discovery.mjs:13-27,35-44` regex-slices `Steam.cs` `ColdStart`, `SessionModes.EnsureSteamDesktop` and C# raw-string `ResidentSetup`; `tools/WsgmLibTest/run-prod-sort.mjs:8-30` and `qam-harness.mjs:38-96` scrape C#. The session and Steam-host refactors move all of them. | Add to the plan (BUILD-016/017). Fragment markers suffice; no manifest (toolkitjs.md C19). |
| C19 | E01 step 2: "Remove stale build comments and symlink dependency using existing external Compile/AvaloniaResource Link includes, preserving resources" (pre-AM01 task-briefs E01) | unverified | `src/WSGM/ThirdParty/LoadingIndicators` is a tracked symlink (mode 120000); `WSGM.csproj:28-36` says it exists so the avares path is a real folder the XAML analyzer resolves. | Keep as an experiment with a build proof and a UI-test run; if `Link` breaks relative theme includes, keep the symlink (BUILD-031). |
| C20 | E01 step 4: "Add/update boundary tests that assert real resource/native/type contracts; remove copied predicate/getter only coverage" | accurate direction | `DeviceBoundaryTests` covers only WSGM's device refs and the Device SDK leaf; no check for Plugin SDK, libraries, launchers or linked sources | BUILD-021 gives the concrete shape. |
| C21 | "Instruction-file changes need a separate concrete diff/human sign-off" (refactor-plan.md:175) | accurate | harness rule; maintainer memory "guidance fixes straight to master" is about process after approval | Ship each stale-guidance diff inside the batch that makes it stale, called out for sign-off (BUILD-B10). |
| C22 | A02-F020 "UWP bridge export validation is an ASCII search" NOT READY | accurate; simple fix available | `build-uwp-bridge.ps1:78-91`; `build-steam-input-lease.ps1:57-104` already runs `dumpbin /exports` through `VsDevCmd` | Reuse that dumpbin path; no PE parser (BUILD-007). |
| C23 | A02-F021 "Common packer admits entry counts and payloads the runtime rejects" NOT READY | accurate | `package-plugin.ps1:52` 4096 entries vs `PluginPackageFile.cs:31-34` (1024 entries, 512 files, 128 MiB, 512 MiB) and an identical copy in `PluginPackageWorkflow.cs:44-47` | One owner `PluginPackageLayout` (sdk.md); packers call it through `plugin-manifest.cs validate-package` (BUILD-014). |
| C24 | A02-F022 "MIT Device Lab compiles generic source from GPL product paths" NOT READY | accurate | `WSGM.DeviceLab.csproj:32-39`; Lab LICENSE is MIT; linked files carry no SPDX header | Maintainer licence decision (open question Q1); the move itself is BUILD-019. |
| C25 | A02-C05 "Preserve authored linked-source declarations ... until exact move inventory/consumer/native ABI/license decisions exist" | accurate | | Section 4 supplies the inventory. |
| C26 | A02_01 step 3: `plugin-manifest.cs` opens with `FileShare.Read`, length check, exact read, growth detection | over-engineered | The helper validates a manifest the packer itself just staged (`package-plugin.ps1:41-45`); `TryRead` rejects oversize after A02_01 step 2. | Keep `File.ReadAllBytes` + `TryRead`; drop the share/growth mechanics (refinement R5). |
| C27 | "Device Lab: ... no runtime policy dependency" (refactor-plan.md:72) | partially (build view) | Lab links `NativeHidHide.cs` and writes the HidHide application list (`Wizard/HidHideAllowance.cs:282-289`) through WSGM's interop; input.md INPUT-032 covers the duplicated read | licence and owner decision with Q1 |

## 2. Findings

Severity scale: critical, high, medium, low, nit. "Covered" cites the ledger/audit id or another domain's finding.

### Gate and CI

**BUILD-001 (high) The canonical gate runs the two unsafe suites.** `eng/verify.ps1:161-173` runs every project under the
slnx `/tests/` folder, which includes `external/windows-device-control/tests/...` (real suspend/restart route,
A01-F002) and `WSGM.Tests` (`ConfigurationTests.cs:621,724,756` open the production `Local\WSGM.Config` mutex,
U04A-LFA-022). The root guide and every plan say "run `.\eng\verify.ps1`", and CI and `release.yml:88-90` run it too. A
cancellation regression in WDC would dispatch a real power action on the maintainer's machine during the gate; the
mutex tests contend with a running WSGM. Covered: A01-F002 (W02_02), U04A-LFA-022 (F01). Recommendation: make W02_02 and
the config-mutex fix hard prerequisites of the first verify run in this refactor; until then the gate is run only with
`--filter` exclusions or not at all. No new mechanism in verify.

**BUILD-002 (medium) `verify.ps1` without `-Fix` rewrites C# and cannot tell cleanup diffs from your own edits.**
`verify.ps1:131-137` always runs `jb cleanupcode` (in place), then `git diff --exit-code -- src tests`. Without `-Fix`
it still leaves rewritten files behind on failure, contradicting `eng/AGENTS.md:17` ("-Fix may rewrite"), and any
unstaged edit under src/tests fails the gate even when it is already clean, so the gate only works on a clean or fully
staged tree. With serialized writers this is survivable but surprising. NEW. Recommendation: capture
`git diff -- src tests` before and after the cleanup and fail only if they differ; state in eng/AGENTS.md that the check
mode also rewrites. Three lines, no new mechanism.

**BUILD-003 (medium) Out-of-solution tools are never compiled.** `tools/DeckSpike` (references `WSGM.csproj`, uses
internal `WSGM.Input.SteamDeckNeptuneReport` and `WSGM.Interop.NativeViiper` through
`[assembly: InternalsVisibleTo("DeckSpike")]`, `src/WSGM/Properties/AssemblyInfo.cs:7`), `tools/OverlayPreview`
(references the `WSGM.UiTests` test project, `WSGM.OverlayPreview.csproj:12`), `LiveBackdropSample`, `PerfLab`,
`HcDeviceExtract`, `SteamReceiver` are outside WSGM.slnx and outside CI. The refactor will move Input/Interop/UI test
infrastructure, so DeckSpike and OverlayPreview break silently. NEW. Recommendation: one verify step that runs
`dotnet build` on `tools/*/*.csproj` (Release, `-p:SkipNativeArtifacts=true`); or delete DeckSpike/SteamReceiver (Q2).

**BUILD-004 (medium) Dependabot can propose Avalonia bumps.** `.github/dependabot.yml:5-13` groups all NuGet minor/patch
updates for `/src/*` and `/tests/*`; Avalonia 12.1.x, FluentAvaloniaUI 3.x, Labs.Panels and ColorPicker are in scope,
contrary to requirement 12. NEW. Recommendation: `ignore: [{dependency-name: "Avalonia*"}, {dependency-name:
"FluentAvaloniaUI"}]`. VIIPER is safe (no `gitsubmodule` ecosystem configured).

**BUILD-005 (medium) Setup payload drops redistributed licence notices.** `WSGM.csproj:88-92` copies `Licenses\*.txt`
(six files) to the publish output, but `build.ps1:143-148` copies an explicit subset into `Payload\App` that omits
`Microsoft.Data.Sqlite-MIT.txt` and `SQLitePCLRaw-Apache-2.0.txt`, while `build.ps1:155-157` ships every `*.dll` including
`Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.*.dll` and `e_sqlite3.dll`. The app is self-contained (`WSGM.csproj` SelfContained)
yet ships no .NET runtime `LICENSE.TXT`/`THIRD-PARTY-NOTICES.TXT`, which Device Lab does for the same reason
(`device-lab-publish.ps1:95-146`). `assert-component-staging.ps1` checks none of this. NEW. Recommendation: payload takes
every `*.txt`/`*.md` notice in `publish\App` instead of a hand list, and the runtime-notice copy from
`device-lab-publish.ps1` becomes one shared function used by both (BUILD-B2).

**BUILD-006 (medium) Controller asset names are hard-coded in three places beside the lock.** `build.ps1:158-161` and
`assert-component-staging.ps1:42-43` name `USBip-0.9.8.1-x64.exe` and `HidHide_1.5.230_x64.exe`;
`assert-controller-pin.ps1:104-108` checks `build.ps1` only for the usbip name. A HidHide bump in
`controller-components.lock.json` passes every check and fails only inside `build.ps1` at copy time. NEW.
Recommendation: both scripts read `asset` from the lock, as `acquire-controller-dependencies.ps1:43-59` does; delete the
build.ps1 text check in `assert-controller-pin.ps1`.

**BUILD-007 (medium) UWP bridge export check is an ASCII search.** `build-uwp-bridge.ps1:78-91`. Covered: A02-F020 (NOT
READY). Simplest correct fix: lift the `vswhere`/`VsDevCmd`/`dumpbin /exports` block from
`build-steam-input-lease.ps1:57-71` into one helper and match the four names in the export table. MSVC is already a hard
requirement of this script, so no new dependency.

**BUILD-008 (low) Three implementations of "download a pinned asset and verify digest and signer".**
`acquire-controller-dependencies.ps1:58-93` (no cache, no retry, writes straight to the destination),
`acquire-pawnio.ps1:30-60,103-118` (cache, `.partial`, three retries), `stage-webview2-runtime.ps1:9-19` (cache, no
`.partial`, subject-regex signer check instead of a thumbprint). NEW. Recommendation: one `Get-PinnedAsset` (url, sha256,
optional thumbprint, cache path, `.partial` then rename) used by all three; keep each lock's current signer rule.

**BUILD-009 (low) `claude.yml` uses moving action tags.** `.github/workflows/claude.yml:29,35` use `actions/checkout@v7`
and `anthropics/claude-code-action@v1` while every other workflow pins a SHA. NEW. Recommendation: pin by SHA.

**BUILD-010 (low) `check-no-live-data-paths.ps1` misses the PowerShell and `%VAR%` forms.** Patterns at
`check-no-live-data-paths.ps1:52-56` do not match `%LOCALAPPDATA%\WSGM` (the `%` breaks `LOCALAPPDATA[\\/]+WSGM`) or
`$env:LOCALAPPDATA`; `tools/PerfLab/perf-capture.ps1:13,41` defaults its 1 GB/30 s trace output to
`%LOCALAPPDATA%\WSGM\perf` and passes. It also cannot see indirect reach through production statics (`Log.Directory`,
parameterless `ConfigStore.Load()`), which is how `SettingsViewModelSplashTests.cs:8-9,173-174` says the real config was
once overwritten. NEW. Recommendation: add the two patterns; default `perf-capture.ps1` output to `artifacts\perf`
(gitignored); rely on the config domain's injected `UserDataContext` for the structural fix rather than growing the scan.

**BUILD-011 (low) `build-steam-input-lease.ps1` documentation and staging disagree with the app.** Lines 12-15 say the
release build skips `-Validate`; `build.ps1:50` passes it. Lines 127-134 stage `steam-input-lease.exe` ("all three must
ship together"), while `WSGM.csproj:59-62` says the exe is deliberately not shipped and only copies `*.dll`, `*.txt`,
`*.md`. NEW. Recommendation: stop staging the exe, fix both comments.

**BUILD-012 (low) WDC builds under WSGM's build configuration only inside this checkout.** C7 above. Covered by the plan
(refactor-plan.md:129) and U01-068; the parent consequence is that `verify.ps1:155` `--warnaserror` enforces WSGM's
`EnforceCodeStyleInBuild` and `.editorconfig` (`IDE0005`, `csharp_prefer_braces`) on WDC while `dotnet format` excludes it.
Recommendation: when WDC gains its own roots, expect different warnings in the parent build; nothing to add here.

### Build fragility and dead mechanism

**BUILD-013 (medium) `check-steam-module-discovery.mjs` asserts C# call order by regex.** Lines 13-27 slice
`private static AppLauncher.LaunchResult ColdStart(` out of `src/WSGM/Core/Steam.cs` and
`public void EnsureSteamDesktop()` out of `src/WSGM/Shell/SessionModes.cs` and match call text; lines 35-44 extract C#
raw-string `ResidentSetup` bodies and `ScriptVersion`. It runs in every verify (`package.json:8`). The session refactor
(session.md SESSION-056 splits `Steam`; `SessionModes` is dissolved) and steamhost.md (download-sort moves to
`Source/download-sort.ts`) break it on rename, or worse keep passing against dead text. It also imports
`external/steam-ui-toolkit/eng/check-harness.mjs` (`:2`), a toolkit-internal file. Covered in part: toolkitjs.md
TOOLKITJS-010/B1 (harness import), steamhost.md (resident script move). The C# regex assertions are NEW. Recommendation:
replace the two C# assertions with a `WSGM.Tests` case on the Steam launch owner using its injected launcher port
(assert `SteamInputShim.Reconcile` and `EnsureRemoteDebuggingEnabled` happen before `Start`); keep the JS module checks,
which then read `Source/download-sort.ts` fragments through the toolkit's fragment markers.

**BUILD-014 (medium) Package rules live in four places.** `PluginPackageFile.cs:31-34`, `PluginPackageWorkflow.cs:44-47`
(byte-identical constants), `package-plugin.ps1:51-62` (4096 entries, managed-PE check), `pack-device.ps1` (delegates to
Device Lab). The host-provided assembly list is duplicated between `PluginPackageLoader.cs:172-179` (`HostOwned`) and
`plugin-package-common.ps1:80-81`. Covered: A02-F021, A02-C02, sdk.md (PluginPackageLayout). Recommendation: as sdk.md,
plus `plugin-manifest.cs validate-package <file>`; `package-plugin.ps1` keeps only publish, strip, stamp, zip and the
validate call. The host-provided names become a public list beside the layout (MIT contract: "assemblies the host always
supplies") that `HostOwned` is tested against and the packer prints through `plugin-manifest.cs host-provided`.

**BUILD-015 (low) Steam asset builder carries dead plugin discovery and a raised-twice size cap.**
`build-steam-assets.mjs:93-110` discovers `src/WSGM.Plugin.*/SteamUiAssets`, none exists, and would compile plugin UI
into the WSGM core asset rather than the package. `:30` "there are none today" is false (nine fragments in
`src/WSGM/Core/SteamUiAssets/Source`). `:128-134` 768 KiB cap is "not a limit anything downstream imposes", raised twice.
Covered: toolkitjs.md TOOLKITJS-010/B1 (drop plugin discovery) and its size-bound note; steamhost.md (hash constant).
Recommendation: delete the discovery and the cap (keep non-empty, no BOM, valid UTF-8); fix the comment; mirror in
`docs/steam-cef-system.md:342`.

**BUILD-016 (low) Live tools scrape C# source and hard-code Steam module ids.** `tools/WsgmLibTest/run-prod-sort.mjs:8-30`
(SteamDownloadSort `ResidentSetup`, `SteamUiBridgeIdentity.Namespace`), `qam-harness.mjs:38-96` (regex over
`Surfaces/*.cs` `PatchId`/`Commands`, `SteamUiSessionHost.cs`), `probe-qam.js:168` (`runtime.m["79476"]`, renumbered by the
September 2026 client per `check-steam-fingerprints.mjs:7-8`), `cdp.mjs:6,10` (`localhost:8080`, while the plan pins
`127.0.0.1`). T01's bridge nonce/identity change and the Steam-host refactor break these silently. NEW. Recommendation:
`run-prod-sort` evaluates the `Source/download-sort.ts` fragment once it moves; `qam-harness` reads the bridge config from
the C#-emitted fixture the plan already requires for toolkit tests (refactor-plan.md:145); `cdp.mjs` uses `127.0.0.1`;
retire the hard-coded-id probes or leave them as historical (Q4).

**BUILD-017 (low) `InternalsVisibleTo("DeckSpike")` on the product.** `src/WSGM/Properties/AssemblyInfo.cs:7` opens the
GPL product's internals to an out-of-solution bench tool that nothing compiles. NEW. Recommendation: keep only if
BUILD-003 compiles DeckSpike; delete with DeckSpike otherwise.

**BUILD-018 (medium) `new-plugin.ps1` templates are uncompiled C# in here-strings.** `eng/new-plugin.ps1:81-166` emits a
common plugin and a GPU plugin (`IPlugin`, `IPluginActions`, `IPluginUi`, `ICapabilityPlugin`,
`CapabilityCommandResult`, `ApplicationProfileSyncResult`). No test runs the script or compiles the output; the Device
Lab scaffold has a build test (`DeviceLabScaffoldingTests.cs:62-173`), the common one does not. Plugin API 4 / Device 12
will leave both templates broken with every gate green. NEW. Recommendation: move both sources to
`eng/templates/CommonPlugin/Plugin.cs` and `eng/templates/GpuPlugin/Plugin.cs` (distinct namespaces, id as the literal
`"__PLUGIN_ID__"`, valid C#) and compile them into `WSGM.Plugin.Sdk.Tests` via `<Compile Include>`; `new-plugin.ps1`
copies and replaces. The F02 API batch then fails to compile until the templates follow.

**BUILD-019 (low) Linked source files live in owner projects.** 14 files are compiled into processes other than their
owner by `<Compile Include>`: `WSGM.Launch.csproj:30-31`, `WSGM.LogonService.csproj:24-29`,
`WSGM.PackagedLaunch.csproj:26-34`, `WSGM.DeviceLab.csproj:32-39`, plus `src/Shared/Gpu` (the one proper shared home). An
edit in `src/WSGM/Core/AtomicFile.cs` or `src/WSGM.Launch/RotatingFileLog.cs` silently changes the logon service; the
"no Log, no ConfigStore, explicit usings" constraint is visible only in file comments and `.editorconfig` special blocks
(`[{src/WSGM/Core/AtomicFile.cs,src/WSGM/Core/BootManifest.cs}]`). Covered: A02-C05 (deferred), A02-F022 (licence part).
Recommendation: move them to `src/Shared/<area>` (Section 4); no new assembly, no props indirection.

**BUILD-020 (low) Two rotating file logs.** `src/WSGM/Core/Log.cs` (static, cross-process `Local\WSGM.LogRotate` mutex,
`.old.log`) and `src/WSGM.Launch/RotatingFileLog.cs` (instance, no mutex, suffix list) both append with
`FileShare.ReadWrite|Delete` and rotate by size. NEW. Recommendation: no merge in this refactor; `RotatingFileLog` moves to
`src/Shared/Process` (BUILD-019). Merging would pull `Log`'s statics into the dependency-light launchers, which the plan
forbids.

### Layering and project graph

**BUILD-021 (medium) Boundary tests guard one edge of a much larger graph.** `DeviceBoundaryTests.cs:9-71` checks that
WSGM does not reference device packages and that Device.Sdk is a leaf; `ContractBoundaryTests.cs:9-20` checks doc
enforcement. Nothing guards: WDC and toolkit referencing no first-party project; plugins referencing only the SDKs (plus
WDC for NVIDIA, and Intel once gpuir.md moves it to WDC); Launch/LogonService/PackagedLaunch having no `ProjectReference`;
`Compile Include` only from `src/Shared` or the steam-input-lease binding; Plugin.Sdk's two references being the only ones.
These are exactly the edges the refactor moves. NEW. Recommendation: one table-driven `ProjectGraphTests` in
`WSGM.Tests/Boundaries` reading every slnx `*.csproj` (it already reads csproj XML) and asserting the allowed matrix in
Section 4; it replaces the three `DeviceBoundaryTests` methods.

**BUILD-022 (low) A tool depends on a test project.** `tools/OverlayPreview/WSGM.OverlayPreview.csproj:12` references
`tests/WSGM.UiTests` (an Exe, self-contained). NEW. Recommendation: keep (it is 16 lines and the preview code is UI-test
infrastructure), but compile it in the gate (BUILD-003).

**BUILD-023 (nit) Tests compile two copies of linked internal types.** `WSGM.Tests.csproj:27,30` references both WSGM and
WSGM.Launch, each grants it `InternalsVisibleTo` and each compiles `WSGM.Core.ScheduledTaskXml` and
`WindowsCommandLine` (`WSGM.Launch.csproj:30-31`). A direct test of either type is CS0433-ambiguous; today the XML is
tested only through `UnelevatedLauncher.BuildTaskXml` (`UnelevatedLauncherTests.cs:9-28`). NEW. Recommendation: give the
Launch reference `Aliases="launch"` so the shared types resolve to WSGM's copy; nothing else.

**BUILD-024 (nit) Mixed target frameworks without a stated rule.** Plugin SDK, Device SDK, Install, Launch, LogonService,
AMD, Intel, IR target `net10.0-windows`; WSGM, Setup, PackagedLaunch, Ally, Claw, HC, NVIDIA, Lab target
`net10.0-windows10.0.19041.0`. Consumers change TFM only when they need WinRT/WDC (gpuir.md moves Intel onto WDC, which
needs 19041). NEW, no change beyond that consumer edit.

### Test infrastructure and test quality

**BUILD-025 (low) Shared test helpers are partly single-consumer and misnamed.** `tests/Shared/SplashConfigBuilder.cs` is
linked only by `WSGM.Tests.csproj:41` and uses `WSGM.Core`; `TemporaryDirectory.cs:13` names every suite's root
`wsgm-device-tests`; `WSGM.UiTests.csproj:24-25` links fakes out of `WSGM.Tests/Fakes` instead of `tests/Shared`, which the
tests guide requires (`tests/WSGM.Tests/AGENTS.md`). Covered: input.md (FakeButtonSource). NEW for the rest.
Recommendation: move `SplashConfigBuilder` to `WSGM.Tests/Builders`; rename the temp root `wsgm-tests`; move the two
shared fakes to `tests/Shared` with the MIT header.

**BUILD-026 (low) Wall-clock polling is the default async test idiom.** `AsyncConditions.WaitForAsync` polls every 10 ms
for up to 10 s (`AsyncConditions.cs:12,33`); 38 call sites across 10 test files, 19 in `SteamUiSessionHostTests`. These
assert eventual state, not order or call counts, and stretch under load. NEW. Recommendation: no helper change; new
lifecycle tests the plan requires (B3 tables) use the owners' tracked tasks and fake clocks; replace polling in a test only
when its owner exposes a completion to await.

**BUILD-027 (low) Two SDK version identities.** SDK csproj versions 0.4.0/0.2.0 (`WSGM.Device.Sdk.csproj:42`,
`WSGM.Plugin.Sdk.csproj:10`) versus the community feed packed as `$wsgmVersion` with a forced `PackageReference Update`
(`build-bundle.ps1:113-130`). NEW. Recommendation: pack the SDKs at their own versions and let the generated targets
`Update` to those versions, so the bumped 0.5.0/0.3.0 is the identity community builds actually see.

**BUILD-028 (low) An eng helper is tested from the Device Lab suite by spawning pwsh.** `tests/WSGM.DeviceLab.Tests/Eng/
DevicePackageOutputTests.cs:9-89` launches PowerShell to test the 10-line `Publish-DevicePackageArchive`
(`plugin-package-common.ps1:5-31`), i.e. `File.Replace`/`File.Move` semantics, and locates the repo through the production
`DeviceLabRepositoryLocator`. NEW. Recommendation: delete the test with the helper when packers simplify (BUILD-B9);
otherwise leave it.

**BUILD-029 (nit) Getter-style tripwires.** `ContractBoundaryTests.TheApiVersionIsPinnedSoRaisingItIsADeliberateAct`
(`:30-37`, asserts the constant equals 11) and `DeviceSdkHasNoProjectOrPackageDependencies` are intentional tripwires, not
behaviour tests. NEW. Recommendation: keep; fold the dependency assertion into BUILD-021's table.

**BUILD-029a (nit) Test-project boilerplate repeated 13 times.** Every test csproj restates `PlatformTarget`,
`IsPackable`, `IsTestProject`, `NoWarn 1573;1591` and the xunit triple; two use xunit.v3 because Avalonia.Headless needs
it (`WSGM.UiTests.csproj:15-17`). NEW. Recommendation: no change (a `tests/Directory.Build.props` would have to import the
root props and adds an MSBuild layer for little gain).

### Over-engineered eng mechanism

**BUILD-030 (low) Four different "owned staging" implementations.** `build-bundle.ps1:63-68,297-313` (temp dir + PID + marker
file), `pack-device.ps1:83-127,241-251` (work marker + per-archive marker file beside the output), `package-plugin.ps1:28-35,
68-74` (create-new + prefix check), `publish-device-lab.ps1:46-204` (marker, ancestor reparse walk, backup, restore,
reparse re-checks before and after). All outputs are under the gitignored `publish/` or a temp dir. NEW. The rules come
from `eng/AGENTS.md:40-42` and `src/WSGM.DeviceLab/AGENTS.md:160`, so changing them is a guidance decision (Q3).
Recommendation: outputs under the repository's `publish/`/`artifacts/` are owned: clear and recreate (as `build.ps1:64-66`
already does for `publish/`); refuse only destinations outside the repository. Removes roughly 250 lines.

**BUILD-031 (nit) Tracked symlink in the build input.** `src/WSGM/ThirdParty/LoadingIndicators` (mode 120000) requires
symlink support at checkout; without it WSGM does not compile. CI and the maintainer's machine have it. Covered: E01 step 2
(unverified). Recommendation: try `AvaloniaResource`/`Compile` with `Link`; keep the symlink if the XAML analyzer then
cannot resolve the relative theme includes (`WSGM.csproj:28-36`).

### Stale comments and documentation

**BUILD-032 (nit) Stale build comments.** `wsgm-revision.targets:3` "(2.0.0)"; `.prettierignore:1` "C# is formatted by
dotnet format" (Rider jb is the authority, `verify.ps1:124-133`); `verify.ps1:101-102` "the installer's fallback version"
(no Inno installer; `check-version-sync.ps1` checks only `app.manifest`); `assert-component-staging.ps1:156`
`WSGM.DeviceHost.exe` (retired); `check-agent-guidance.ps1:1` and `assert-component-staging.ps1:1` open with `<#[`;
`publish-device-lab.ps1:140-204` try body not indented; `pack-device.ps1:154,234` dot-sources the same helper twice.
Covered: PV08-011 (Directory.Build.props AVLN3001 comment). NEW for the rest.

**BUILD-033 (nit) `update-ui-baselines.ps1` restates every case name.** `:4` is a hand-maintained regex of all 51 baseline
names; adding a case means editing it. NEW. Recommendation: accept any `-Case` that is a plain file name and whose
`TestResults/ui/<case>/actual.png` exists (the script already requires that).

**BUILD-034 (low) Guidance and docs that are already wrong.** NEW unless cited.
- `AGENTS.md:57` "WSGM.slnx.DotSettings carries the inspection overrides": it carries one
  (`RedundantUnsafeContext`); the overrides are in `.editorconfig`, as `eng/AGENTS.md:13-14` says.
- `tests/WSGM.Tests/AGENTS.md:3` describes coverage of "the main application, launcher, and logon service"; it also
  covers Setup, PackagedLaunch, the IR package host and the toolkit (`WSGM.Tests.csproj:26-32`). Line 7 forbids named
  resources; `ConfigurationTests` uses the production mutex (U04A-LFA-022).
- `src/WSGM.Plugin.Sdk/README.md:13` "depends on nothing" (A02-F002).
- `docs/ui.md:112-115` lists baseline areas that omit Steam, Tools, Power, Keyboard, Power menu, Profiles, Graphics and
  Sections, which all have baselines.
- `docs/logging.md:10,28` documents `PluginTrace`, which F02 replaces with `PluginDiagnostics`.
- `docs/steam-cef-system.md:342` documents the 768 KiB cap (BUILD-015).
- `src/WSGM.PackagedLaunch/AGENTS.md:39,61-63`, `src/WSGM.DeviceLab/README.md:238-239` name linked-file paths that
  BUILD-019 moves.

**BUILD-035 (nit) `dev-deploy.ps1` runs a user-writable script elevated.** `dev-deploy.ps1:259-263` writes
`publish\dev-deploy-swap.ps1` and starts it with `-Verb RunAs`; a same-user process can swap the file between write and
elevation. Attended dev tool on the maintainer's machine, so nit. NEW. Recommendation: pass the swap script with
`-EncodedCommand` and the request path as an argument; no file to swap.

**BUILD-036 (nit) Duplicated repository-root locators.** `tests/Shared/RepositoryFiles.Root` and production
`DeviceLabRepositoryLocator` (`OutputPathPolicy.cs:287`) both walk up for the checkout; the Lab tests use the production
one. NEW, no change (different markers, different owners).

## 3. Plan refinements

Additions
- R1. Gate safety order: W02_02 (WDC power port) and the config-mutex test fix are hard prerequisites of the first
  `eng/verify.ps1` run in this refactor (BUILD-001). Until then, test runs use explicit `--filter` sets as the planning
  baseline did.
- R3. Add a tools compile step to the gate (BUILD-003) so the plan's "no parent compile gap" covers DeckSpike,
  OverlayPreview and the other tools.
- R4. Add Dependabot ignores for the frozen Avalonia family (BUILD-004) to the frozen-foundation acceptance.
- R6. Add `ProjectGraphTests` (BUILD-021) as the structural acceptance for every move in this refactor; it is cheaper and
  more precise than per-batch symbol inventories for project-level edges.
- R7. Add the notice fix (BUILD-005) and the lock-driven controller asset names (BUILD-006) before anyone "keeps existing
  notices/pins".
- R8. Add the uncompiled-template fix (BUILD-018) ahead of the Device 12 / Plugin 4 API batch so that batch cannot finish
  with broken templates.
- R9. Add the C#-source scrapers (BUILD-013, BUILD-016) to the session and Steam-host consumer lists; each refactor that
  moves `Steam.cs`, `SessionModes.cs`, `SteamDownloadSort.cs` or the bridge identity must update them or replace them.

Changes
- R2. Replace "parent may be nonbuilding until I01" and the I01/I02 publication phases (api-integration.md, R2-3) with one
  rule per library batch: change the child and adapt every in-repo consumer in the same working tree, keep the solution
  building, commit and push the child, then commit the parent gitlink with the consumer changes. This is what CLAUDE.md
  already requires and keeps every batch green.
- R10. Coverage: drop "do not lower coverage thresholds" (no threshold exists) and do not introduce one.
- R11. Stale-guidance fixes ride in the batch that makes the guidance stale, shown as a separate diff for sign-off, rather
  than deferred to a final Z02 proposal.

Removals (over-engineering under the simplify / no-arbitrary-limits rules)
- R5. A02_01 step 3 (`plugin-manifest.cs` share mode, exact-length read, growth detection): drop; `File.ReadAllBytes` plus
  the bounded `TryRead` is enough for a packer validating its own staged file.
- R12. `build-steam-assets.mjs` 768 KiB cap: remove (it bounds nothing downstream and has been raised twice); keep the
  non-empty/BOM/UTF-8 checks (agrees with toolkitjs.md).
- R13. Dead plugin `SteamUiAssets` discovery in the asset builder: remove.
- R14. Owned-staging ceremony (`publish-device-lab.ps1` marker/backup/reparse dance, pack-device output markers, bundle
  PID markers): replace with "repository-owned output is cleared and recreated; anything outside the repository is
  refused" (needs Q3).
- R15. Three pinned-download implementations and two export checkers: one helper each (BUILD-007/008).
- R16. Keep, do not add: no central package management (would collide with the deliberately different Avalonia versions
  per project), no `tests/Directory.Build.props`, no new shared assembly for linked sources, no new live-data scanner
  logic beyond two regexes.

## 4. Target design

### 4.1 Project graph (asserted by `ProjectGraphTests`)

| Project | Allowed `ProjectReference` | Allowed linked `Compile` |
| --- | --- | --- |
| WDC, SteamUiToolkit | none | none |
| WSGM.Device.Sdk | none (no packages) | none |
| WSGM.Plugin.Sdk | Device.Sdk, SteamUiToolkit | none |
| WSGM.Install | Device.Sdk | `src/Shared/Install/InstallLayout.cs` |
| Avalonia.LiveBackdrop | none | none |
| WSGM | LiveBackdrop, Device.Sdk, Plugin.Sdk, Install, WDC, SteamUiToolkit | `src/Shared/**` it consumes, steam-input-lease binding |
| WSGM.Setup | Install | WSGM themes as `AvaloniaResource` links (unchanged) |
| WSGM.Launch | none | `src/Shared/Process/*`, steam-input-lease binding |
| WSGM.LogonService | none | `src/Shared/Boot/*`, `src/Shared/Install/InstallLayout.cs`, `src/Shared/Process/{Win32Common,RotatingFileLog}.cs` |
| WSGM.PackagedLaunch | none | `src/Shared/Launch/PackagedLaunchCommand.cs`, `src/Shared/Process/{ParentProcessStart,Win32Common,SteamControllerExclusion,RotatingFileLog}.cs` |
| Device packages (Claw, Ally, HC) | Device.Sdk | none |
| WSGM.DeviceLab | Device.Sdk | `src/Shared/Interop/*` (after Q1) |
| GPU packages | Plugin.Sdk; NVIDIA (and Intel after gpuir.md) also WDC | `src/Shared/Gpu/*` (Intel added by gpuir.md) |
| WSGM.Plugin.Ir | Plugin.Sdk | none |
| Test projects | their subject + SDKs; `WSGM.Tests` also Launch (aliased), PackagedLaunch, Ir, Setup, toolkit | `tests/Shared/*`, `LogonDecision.cs` |

### 4.2 Linked sources: old path to new owner

No symbol, namespace or behaviour changes; files move, every consumer `Compile Include` path changes, `.editorconfig`
blocks follow the files, each file's header names its consumers and the "no Log/ConfigStore, explicit usings" rule.

| Old path | New path | Compiled into |
| --- | --- | --- |
| `src/WSGM/Core/ScheduledTaskXml.cs` | `src/Shared/Process/ScheduledTaskXml.cs` | WSGM, Launch (plan line 107 later changes its consumer to a COM adapter in WSGM; the file stays shared while Launch needs it) |
| `src/WSGM/Core/WindowsCommandLine.cs` | `src/Shared/Process/WindowsCommandLine.cs` | WSGM, Launch |
| `src/WSGM/Interop/Win32Common.cs` | `src/Shared/Process/Win32Common.cs` | WSGM, LogonService, PackagedLaunch |
| `src/WSGM/Interop/ParentProcessStart.cs` | `src/Shared/Process/ParentProcessStart.cs` | WSGM, PackagedLaunch |
| `src/WSGM.Launch/SteamControllerExclusion.cs` | `src/Shared/Process/SteamControllerExclusion.cs` | Launch, PackagedLaunch |
| `src/WSGM.Launch/RotatingFileLog.cs` | `src/Shared/Process/RotatingFileLog.cs` | Launch, LogonService, PackagedLaunch |
| `src/WSGM/Core/BootManifest.cs` | `src/Shared/Boot/BootManifest.cs` | WSGM, LogonService |
| `src/WSGM/Core/AtomicFile.cs` | `src/Shared/Boot/AtomicFile.cs` | WSGM, LogonService |
| `src/WSGM.Install/InstallLayout.cs` | `src/Shared/Install/InstallLayout.cs` | Install, LogonService |
| `src/WSGM/Core/PackagedLaunchCommand.cs` | `src/Shared/Launch/PackagedLaunchCommand.cs` | WSGM, PackagedLaunch |
| `src/WSGM/Interop/Kernel32.cs` | `src/Shared/Interop/Kernel32.cs` | WSGM, DeviceLab (after Q1) |
| `src/WSGM/Interop/NativePackageSource.cs` | `src/Shared/Interop/NativePackageSource.cs` | DeviceLab only today (library.md: WSGM compiles it with no consumer; drop it from WSGM) |
| `src/WSGM/Interop/NativePathIdentity.cs` | `src/Shared/Interop/NativePathIdentity.cs` | WSGM, DeviceLab (after Q1) |
| `src/WSGM/Interop/NativeHidHide.cs` | `src/Shared/Interop/NativeHidHide.cs` | WSGM, DeviceLab (after Q1; input.md INPUT-032 merges `NativeHidHide.Paths.cs` afterwards) |
| (session.md) new `SessionProtocolNames.cs` | `src/Shared/Process/SessionProtocolNames.cs` | WSGM, Setup |

WSGM keeps compiling the files it uses through `<Compile Include="..\Shared\...">` like the launchers; `src/WSGM`
loses the physical files (its default glob no longer sees them).

### 4.3 eng helpers

| Old location | New owner |
| --- | --- |
| `acquire-pawnio.ps1` `Test-Pinned`, `Get-Pinned`; `acquire-controller-dependencies.ps1` download/verify loop; `stage-webview2-runtime.ps1` download/verify | `eng/build-common.ps1` `Get-PinnedAsset` (new file, dot-sourced) |
| `build-steam-input-lease.ps1:57-71` vswhere/VsDevCmd/dumpbin; `build-uwp-bridge.ps1:78-91` ASCII search | `eng/build-common.ps1` `Get-DllExports`; both scripts match against its output |
| `device-lab-publish.ps1:95-146` runtime notices | `eng/build-common.ps1` `Copy-RuntimeNotices`, used by `Publish-DeviceLab` and `build.ps1` |
| `build.ps1:143-148` licence name list | glob of `*.txt`/`*.md` notices in `publish\App` |
| `build.ps1:158-161`, `assert-component-staging.ps1:42-43` controller asset names | read from `external/controller/controller-components.lock.json` |
| `package-plugin.ps1:51-62` entry/PE checks | `plugin-manifest.cs validate-package` over SDK `PluginPackageLayout` (sdk.md) |
| `new-plugin.ps1:81-166` here-string templates | `eng/templates/CommonPlugin/Plugin.cs`, `eng/templates/GpuPlugin/Plugin.cs`, compiled by `WSGM.Plugin.Sdk.Tests` |
| `check-steam-module-discovery.mjs:13-27` C# regexes | `WSGM.Tests` Steam launch ordering test (session domain owner) |
| `build-steam-assets.mjs:93-110,128-134` | deleted |

### 4.4 Public API changes in this domain

- `WSGM.Device.Sdk.Packaging.PluginPackageLayout` (sdk.md) gains `HostProvidedAssemblies` (string names).
  Consumers: `PluginPackageLoader.PluginLoadContext.HostOwned` (test asserts equality of names), `plugin-package-common.ps1`
  `Remove-HostProvidedFiles` (reads via `plugin-manifest.cs host-provided`), Device Lab pack/validate.
- `eng/plugin-manifest.cs` gains `validate-package <file>` and `host-provided`. Consumers: `package-plugin.ps1`,
  `pack-device.ps1` (optional, Device Lab already validates), `build-bundle.ps1`.
- No other public API changes. The new-plugin template output changes namespace (`ExamplePlugin.Common` /
  `ExamplePlugin.Gpu`) and its manifest `entryType` with it.

## 5. Implementation batches

All batches keep the solution building. Tests named are the narrow filters; the full gate runs once at the end
(BUILD-B11), after R1's safety prerequisites. Line counts are rough changed lines.

**BUILD-B1 Gate and CI hygiene (~120).** Files: `eng/verify.ps1`, `.github/dependabot.yml`, `.github/workflows/claude.yml`,
`eng/check-no-live-data-paths.ps1`, `tools/PerfLab/perf-capture.ps1`, `eng/AGENTS.md` (diff for sign-off). Steps:
before/after diff around `jb cleanupcode` (BUILD-002); Dependabot Avalonia/FluentAvaloniaUI ignore (BUILD-004); pin
`claude.yml` actions (BUILD-009); two extra live-data patterns and `artifacts\perf` default (BUILD-010); stale comments in
`verify.ps1:101`, `.prettierignore:1`, `wsgm-revision.targets:3`, `<#[` typos (BUILD-032). Dependencies: none. Tests:
`./eng/check-no-live-data-paths.ps1`, `./eng/check-agent-guidance.ps1`, PowerShell parse of changed scripts.

**BUILD-B2 Release payload truth (~180).** Files: `build.ps1`, `eng/assert-component-staging.ps1`,
`eng/assert-controller-pin.ps1`, `eng/build-steam-input-lease.ps1`, `eng/device-lab-publish.ps1`, new
`eng/build-common.ps1` (`Copy-RuntimeNotices` only in this batch). Steps: notices by glob plus runtime notices
(BUILD-005); lock-driven controller names (BUILD-006); drop `WSGM.DeviceHost.exe`; stop staging `steam-input-lease.exe`,
fix comments (BUILD-011). Dependencies: none. Tests: script parse; `./eng/assert-controller-pin.ps1`. The setup build
itself (`build.ps1`) is the maintainer's manual check; say so.

**BUILD-B3 Pinned downloads and export checks (~220).** Files: `eng/build-common.ps1`,
`acquire-controller-dependencies.ps1`, `acquire-pawnio.ps1`, `stage-webview2-runtime.ps1`, `build-steam-input-lease.ps1`,
`build-uwp-bridge.ps1`. Steps: `Get-PinnedAsset` (BUILD-008), `Get-DllExports` (BUILD-007, closes A02-F020). Dependencies:
B2 (shared file). Tests: `./eng/assert-pawnio-pin.ps1`; run `build-uwp-bridge.ps1 -Validate` and
`build-steam-input-lease.ps1 -Validate` locally (non-invasive builds).

**BUILD-B4 Template compile coverage (~250).** Files: `eng/new-plugin.ps1`, new `eng/templates/{CommonPlugin,GpuPlugin}/
Plugin.cs`, `tests/WSGM.Plugin.Sdk.Tests/WSGM.Plugin.Sdk.Tests.csproj`, `docs/plugin-system.md:223-250`. Steps:
BUILD-018. Dependencies: must land before the SDK domain's Device 12 / Plugin 4 batch. Tests:
`dotnet build tests/WSGM.Plugin.Sdk.Tests/WSGM.Plugin.Sdk.Tests.csproj -c Release --warnaserror`.

**BUILD-B5 Project graph test (~200).** Files: `tests/WSGM.Tests/Boundaries/ProjectGraphTests.cs` (replaces
`DeviceBoundaryTests.cs`), `tests/WSGM.Device.Sdk.Tests/Boundaries/ContractBoundaryTests.cs` (dependency assertion moves
out). Steps: BUILD-021 with today's matrix (linked sources still at old paths), so it passes now and every later move
updates one table row. Dependencies: none. Tests:
`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Boundaries"`.

**BUILD-B6 Linked sources to `src/Shared` (~350, mostly moves).** Files: the 14 files in 4.2, `WSGM.csproj` (explicit
includes), `WSGM.Launch.csproj`, `WSGM.LogonService.csproj`, `WSGM.PackagedLaunch.csproj`, `WSGM.Install.csproj`,
`WSGM.DeviceLab.csproj` (only after Q1), `.editorconfig` blocks, `.gitattributes` unaffected (`src/**/*.cs`), AGENTS/README
path mentions (diff for sign-off), `ProjectGraphTests` rows, `WSGM.Tests.csproj` Launch alias (BUILD-023). Dependencies:
Q1 for the four interop files (the rest can go first); session domain's `SessionProtocolNames.cs` lands directly in
`src/Shared/Process`; winsvc domain if it is editing LogonService in the same period. Tests:
`dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true` and
`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Boundaries|FullyQualifiedName~Launch|FullyQualifiedName~Logon|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~BootManifest|FullyQualifiedName~AtomicFile"`.

**BUILD-B7 Package policy consumers (~200).** Files: `eng/plugin-manifest.cs`, `eng/package-plugin.ps1`,
`eng/plugin-package-common.ps1`, `eng/build-bundle.ps1` (SDK feed version, BUILD-027), `docs/plugin-system.md`,
`docs/device-plugin-authoring.md`. Steps: `validate-package`, `host-provided`, remove the PS-side PE/entry checks
(BUILD-014). Dependencies: sdk.md's `PluginPackageLayout` batch; A02_01 without step 3 (R5). Tests:
`dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj --filter "FullyQualifiedName~Manifest"`,
`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginPackage"`, and a local
`eng/package-plugin.ps1` run on `src/WSGM.Plugin.Ir` into a temp path.

**BUILD-B8 Source scrapers and tools (~300).** Files: `eng/check-steam-module-discovery.mjs`, `package.json`,
`tools/WsgmLibTest/{cdp,run-prod-sort,qam-harness}.mjs`, `eng/verify.ps1` (tools compile step, BUILD-003),
`src/WSGM/Properties/AssemblyInfo.cs` (only if DeckSpike is deleted), `eng/build-steam-assets.mjs` (BUILD-015).
Dependencies: session domain's Steam launch port (for the replacement C# ordering test), steamhost.md's download-sort move
to `Source/download-sort.ts`, toolkitjs.md B1 (fragment markers, `run-checks.mjs` for claims), T01 bridge identity for
qam-harness. Tests: `npm run steam-assets:check`, `npm run steam-assets:claims`,
`dotnet build tools/DeckSpike -c Release -p:SkipNativeArtifacts=true` (and the other tools), the new Steam launch test
filter from the session domain.

**BUILD-B9 Staging simplification (~-250 net).** Files: `eng/publish-device-lab.ps1`, `eng/pack-device.ps1`,
`eng/build-bundle.ps1`, `eng/plugin-package-common.ps1`, `tests/WSGM.DeviceLab.Tests/Eng/DevicePackageOutputTests.cs`,
`eng/AGENTS.md:40-42`, `src/WSGM.DeviceLab/AGENTS.md:160`. Steps: R14, BUILD-028. Dependencies: Q3 yes. Tests: script parse;
local `./eng/publish-device-lab.ps1` into `publish/DeviceLab` and `./eng/pack-device.ps1 -Source src/WSGM.Device.Msi.Claw
-RequireGlyphs` (offline, no hardware).

**BUILD-B10 Test helpers and docs (~200).** Files: `tests/Shared/*`, `WSGM.Tests/Builders/SplashConfigBuilder.cs`,
`WSGM.UiTests.csproj`, `eng/update-ui-baselines.ps1` (BUILD-033), `docs/ui.md`, `docs/logging.md` (after F02),
`docs/steam-cef-system.md:342`, `src/WSGM.Plugin.Sdk/README.md:13` (A02_DOC), `AGENTS.md:57` and
`tests/WSGM.Tests/AGENTS.md:3,7` (diff for sign-off). Dependencies: input.md for `FakeButtonSource`; F02 for the logging
text. Tests: `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~Settings"`,
`npm run format:check`.

**BUILD-B11 Final gate (no source).** After W02_02, the config-mutex fix and every domain batch: `.\eng\verify.ps1` once
(now including the tools compile), then the maintainer's `build.ps1` setup build and manual matrix. Deferred until then:
the full gate, coverage, UI baselines, native payload validation.

Order: B1, B2, B5, B4 (before the SDK API batch), B3, B6, B7 (after sdk.md), B8 (after session/steamhost/toolkitjs), B9
(after Q3), B10, B11.

## 6. Risks and open questions

Risks
- Running `eng/verify.ps1` before W02_02 and the config-mutex fix can dispatch a real power action or contend with a live
  WSGM (BUILD-001).
- Moving linked sources touches four process boundaries at once; a missing include fails only the affected exe's build,
  which `dotnet build WSGM.slnx` catches; `ProjectGraphTests` catches a wrong include path.
- The tools compile step adds restore time (PerfLab pulls the large TraceProcessing package); acceptable once per gate.

Open questions (maintainer decision)
- Q1. Licence of the four interop files Device Lab compiles (`Kernel32`, `NativePackageSource`, `NativePathIdentity`,
  `NativeHidHide`): MIT so Lab stays MIT, or keep GPL and accept Lab compiling GPL source (A02-F022)?
- Q2. Keep `tools/DeckSpike` and `tools/SteamReceiver` (and compile them in the gate) or delete them now that the
  2026-09-28 trigger experiment is answered (removes `InternalsVisibleTo("DeckSpike")`)?
- Q3. May the owned-staging rules in `eng/AGENTS.md:40-42` and `src/WSGM.DeviceLab/AGENTS.md:160` be replaced by "repository
  output is cleared and recreated; outside the repository is refused" (BUILD-030, ~250 lines removed)?
- Q4. Retire the `tools/WsgmLibTest/probe-*.js` live module-registry sweeps with hard-coded module ids in favour of the
  offline `eng/check-steam-fingerprints.mjs`, or keep them as attended history?
