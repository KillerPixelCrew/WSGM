# Build, tooling and test infrastructure findings

Scope: the project graph and layering (`WSGM.slnx`, every first-party `*.csproj`, `Directory.Build.props`, `.editorconfig`, `.gitattributes`), linked source files shared between processes, `eng/**` scripts, `build.ps1`, `.github/**`, `tools/**`, `tests/Shared` and the cross-cutting test infrastructure, plus build-related docs and guidance. Sources: `_plan/refactor-2.1/review/build.md` (37 findings), its adversarial verification `build.verify.md` (no finding refuted, eight corrected, five missed findings added), the critic `_critic.md` (conflicts 21 and 22, item 2.4) and plan v2, which wins wherever it changed a recommendation, and the maintainer's answers in `_plan/refactor-2.1/DECISIONS.md` (2026-10-03), which win over all of them. Those corrections are already applied below.

Counts: 43 ids. 35 have a section below: 0 critical, 0 high, 9 medium, 19 low, 7 nit. Eight more (four low, four nit) need no change and are listed at the end. The verifier lowered BUILD-001 from high to medium, and BUILD-002 and BUILD-006 from medium to low. The solution check (2026-10-03) moved BUILD-027 and BUILD-035 to no-change, added BUILD-C-001, and rewrote BUILD-030 and BUILD-028 because the original rule would have broken the bundle build, `pack-device.ps1`'s default output and the documented third-party `package-plugin.ps1` workflow. The maintainer's decisions (2026-10-03) moved BUILD-009 to no-change (security hardening dropped), settled D2, D3, D4 and D12 for this area, and changed BUILD-019: the interop files stay GPL and Device Lab becomes GPL (D3).

Corrections that override `batches.json` and plan v2 text: B032 drops the BUILD-009 item; B147 drops the BUILD-027 item; B176 drops the BUILD-035 item; B173's "five test files" for the Launch alias are two (BUILD-023); B173 follows D3 as decided (no MIT relicensing of `Kernel32`, `NativePathIdentity` or `NativeHidHide`, which plan v2's D3 row proposed); B175 follows the rewritten BUILD-030 rule, not "anything outside the repository is refused".

Plan v2 batches for this area, in execution order:
- Safety prerequisites owned by other domains: B002 (WDC power port), B037 (config tests on a private mutex) and B070 (topology wait test removed).
- Phase C build foundations: B032, B033, B034, B035, B036.
- B046 (WDC child build roots) and B147 (SDK package layout and packers).
- Phase G: B173 (linked sources; decided: D3, Device Lab becomes GPL), B174 (scrapers and tools; decided: D12, delete DeckSpike and SteamReceiver), B175 (staging; decided: D4, guidance diffs approved) and B176 (helpers and docs).
- B179: the frozen-foundation check and the one full `eng/verify.ps1` run.

Plan-level corrections from this review that plan v2 already adopted, and that every batch follows:
- Library publication is per batch (critic conflict 21, CLAUDE.md). The child changes and every in-repo consumer adaptation sit in one working tree. Commit and push the child first, then make one parent commit with the gitlink and the consumer edits. The parent builds after every batch. There are no I01/I02 phases and no tree-equality step, but every child batch still runs its own child-local validation.
- No coverage threshold exists, and none is added.
- A02_01 step 3 (share mode, exact-length read, growth detection in `plugin-manifest.cs`) is dropped.
- `eng/verify.ps1` is never run on an uncommitted tree.

Line numbers are from `master` 1329813f and drift. Anchor every edit by symbol or by literal text.

### BUILD-001: the canonical gate runs the WDC real-power test and the production config-mutex tests

- **Severity:** medium (the verifier lowered it from high: neither hazard fires on today's code).
- **Where:** `eng/verify.ps1` (the test loop near the end enumerates every project in the `/tests/` slnx folder, which includes `external/windows-device-control/tests/WindowsDeviceControl.Tests`); `external/windows-device-control/tests/WindowsDeviceControl.Tests/WindowsPowerTests.cs` (`CancelledActionsNeverDispatch`); `tests/WSGM.Tests/Core/ConfigurationTests.cs` (three tests that open `Local\WSGM.Config`); the WDC `DisplayTopologyTests` test that calls the real public topology wait (A01-F006); `.github/workflows/ci.yml` and `release.yml` run `eng/verify.ps1`.
- **Problem:** `CancelledActionsNeverDispatch` reaches the real `SetSuspendState` and `shutdown.exe` paths. A regression in cancellation admission would suspend or restart the maintainer's machine during the gate. The three config tests contend with a running WSGM for 200 to 2000 ms, which makes them flaky and can briefly block a real config save. They do not lose data. The topology test depends on hardware the same way once it regresses. The root guide and every plan say "run `.\eng\verify.ps1`", so the gate is not safe to run on the dev machine until all three are fixed.
- **Best solution:** add no mechanism to `verify.ps1`. Fix the three tests where they live and run the full gate only after all three fixes:
  - B002 (power half): WDC `IPowerActionApi` is an internal port with a private native implementation. The four fake-backed tests replace `CancelledActionsNeverDispatch`, so no test can reach native power APIs.
  - B037 (config half): `UserDataContext(Root, ConfigMutexName)`. Tests use a temp root and `Local\WSGM.Tests.Config.<guid>`, so no test opens the production mutex.
  - B070: the WDC `AvailableWait` and topology wait test that reached native code is deleted along with the waits.
  - Until all three have landed, batches run only their narrow `--filter` sets (protocol step 4). B179 runs `.\eng\verify.ps1` once, on the committed head.
- **Tests:** owned by B002 (`WindowsPowerTests` filter, net8), B037 (`FullyQualifiedName~Configuration`) and B070. Nothing new in the build area.
- **Plan v2:** B002, B037, B070 are the prerequisites; B179 is the gate run.
- **Related:** A01-F002, A01-F006, U04A-LFA-022, WDC-013; BUILD-V-002.

### BUILD-003: the six out-of-solution tools are never compiled

- **Severity:** medium.
- **Where:** `tools/DeckSpike/DeckSpike.csproj` (references `src/WSGM/WSGM.csproj` and uses internals through `src/WSGM/Properties/AssemblyInfo.cs` `[assembly: InternalsVisibleTo("DeckSpike")]`); `tools/OverlayPreview/WSGM.OverlayPreview.csproj` (references `tests/WSGM.UiTests`); `tools/LiveBackdropSample`, `tools/PerfLab/WSGM.PerfLab.csproj`, `tools/HcDeviceExtract`, `tools/SteamReceiver`; `eng/verify.ps1` and `ci.yml` build only `WSGM.slnx`.
- **Problem:** this refactor moves Input, Interop and the UI test infrastructure, so OverlayPreview, and DeckSpike while it exists, break without any gate noticing. The plan's "no parent compile gap" does not cover them.
- **Best solution:** D12 is decided: `tools/DeckSpike` and `tools/SteamReceiver` are deleted in the same batch, together with `InternalsVisibleTo("DeckSpike")` (BUILD-017). Then extend `eng/verify.ps1`:
  - Enumerate the tools the way verify already enumerates PowerShell files: `$toolProjects = @(git ls-files -- "tools/*.csproj")` (tracked files only, so `bin`/`obj` never appear), and throw if the list is empty.
  - Next to `dotnet restore WSGM.slnx -m:1`, run `dotnet restore <tool> -m:1` for each.
  - After the test loop (not between the solution build and the tests), run `dotnet build <tool> --configuration Release --no-restore -m:1` for each tool and throw on a non-zero exit. Pass no extra `-p:` property: the solution build used none, so the referenced WSGM, UiTests and LiveBackdrop builds are up to date and reused. A different global property such as `SkipNativeArtifacts=true` would build those projects a second time as a separate instance, and its `IncrementalClean` could delete the native files (`steam_input_gate.dll`, `libviiper.dll`) the first build copied into their output folders.
  - Leave `--warnaserror` off. The step guards compilation, and tool warnings are not product warnings.
  - Keep the tools out of `WSGM.slnx`. Adding a `/tools/` folder would put them under `dotnet format`, the solution `--warnaserror` build and `ProjectGraphTests`, which costs more than a separate build loop.
- **Tests:** run the new verify step's commands by hand for each remaining tool (`HcDeviceExtract`, `LiveBackdropSample`, `OverlayPreview`, `PerfLab`). Defer the full verify run to B179.
- **Plan v2:** B174, decided: D12, delete DeckSpike and SteamReceiver.
- **Related:** BUILD-V-005 (restore cost), BUILD-017, BUILD-022 (no change).

### BUILD-004: nothing enforces the Avalonia and VIIPER freeze, and Dependabot can propose Avalonia bumps

- **Severity:** medium (the verifier narrowed the recommendation).
- **Where:** `.github/dependabot.yml` (the `nuget` ecosystem for `/src/*` and `/tests/*` groups all minor and patch updates with no ignore list); Avalonia references in `src/WSGM/WSGM.csproj`, `src/WSGM.Setup`, `src/Avalonia.LiveBackdrop`, `tests/WSGM.UiTests`, `tools/LiveBackdropSample` (12.1.2) and `src/WSGM.DeviceLab/WSGM.DeviceLab.csproj` (12.1.3, from the Dependabot PR #186); FluentAvaloniaUI 3.1.0, Avalonia.Labs.Panels 12.0.2, ColorPicker 12.1.2, Headless and Skia 12.1.2.
- **Problem:** requirement 12 freezes the Avalonia packages and VIIPER for this refactor, but no check enforces it. Dependabot is active and has already moved one project. A merged bump during the refactor would go unnoticed.
- **Best solution:** leave `.github/dependabot.yml` alone. A permanent ignore would change the maintainer's standing dependency policy beyond this refactor. Enforce the freeze with the critic's diff check in B179 instead. On the committed head, run `git diff 1329813f -- '*.csproj' Directory.Build.props external/viiper` and confirm it shows no `Avalonia*`, `FluentAvaloniaUI` or `Avalonia.Labs*` version change and no VIIPER gitlink change. The baseline already contains the Device Lab 12.1.3 reference, so that difference is not a change. A project file that a batch moved or created shows its Avalonia lines as added; that is not a version change as long as each version equals the one the same package had at 1329813f (12.1.2, Device Lab 12.1.3, FluentAvaloniaUI 3.1.0, Avalonia.Labs.Panels 12.0.2). If the diff shows a real version change, revert that line. The maintainer simply does not merge Avalonia Dependabot PRs until 2.1.0 ships.
- **Tests:** the diff check itself, in B179.
- **Plan v2:** B179 (no Dependabot edit; B032 explicitly leaves it out).
- **Related:** critic 2.4; requirement 12.

### BUILD-005: the setup payload drops two shipped library licences and all .NET runtime notices

- **Severity:** medium.
- **Where:** `src/WSGM/WSGM.csproj` (`<None Include="Licenses\*.txt">` publishes six licence files); `build.ps1` (`$appFiles` list in "Assembling the setup payload"); the `*.dll` glob in the same block ships `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.*.dll` and `e_sqlite3.dll`; `eng/device-lab-publish.ps1` (`Publish-DeviceLab`, the runtime-notice block from `$assetsPath` to the copy loop); `eng/assert-component-staging.ps1`.
- **Problem:** `$appFiles` names four of the six licence files and omits `Microsoft.Data.Sqlite-MIT.txt` and `SQLitePCLRaw-Apache-2.0.txt`, although the setup ships those DLLs. WSGM is self-contained (`<SelfContained>true</SelfContained>`) but ships no .NET runtime `LICENSE.TXT` or `THIRD-PARTY-NOTICES.TXT`. Device Lab does ship them, for the same reason. Nothing checks either gap.
- **Best solution:** keep the explicit list. Globbing `publish\App` would also pull `VIIPER-LICENSE.txt` and `VIIPER-NOTICE.md` into `Payload\App`, but they belong in `Payload\Controller`.
  - New `eng/build-common.ps1` (dot-sourced) with `Copy-RuntimeNotices -AssetsPath <obj\project.assets.json> -RuntimeIdentifier <rid> -Destination <dir>`. Lift the body unchanged from `Publish-DeviceLab`: resolve the exact `Microsoft.NETCore.App.Runtime.<rid>` pack from the assets file, then copy `LICENSE.TXT` to `DotNetRuntime-LICENSE.txt` and `THIRD-PARTY-NOTICES.TXT` to `DotNetRuntime-THIRD-PARTY-NOTICES.txt`, keeping the reparse-point refusal. Device Lab's own `LICENSE` copy stays in `device-lab-publish.ps1`, which now calls the helper for the two runtime files.
  - `build.ps1`, after `dotnet publish` of WSGM: `Copy-RuntimeNotices -AssetsPath "$root\src\WSGM\obj\project.assets.json" -RuntimeIdentifier win-x64 -Destination $appPublish`. The RID-aware `dotnet restore WSGM.slnx --runtime win-x64` above it already produces that assets file.
  - Add `Microsoft.Data.Sqlite-MIT.txt`, `SQLitePCLRaw-Apache-2.0.txt`, `DotNetRuntime-LICENSE.txt` and `DotNetRuntime-THIRD-PARTY-NOTICES.txt` to `$appFiles`, and add one `Require-File "App\<name>"` per name to `assert-component-staging.ps1`.
- **Tests:** a PowerShell parse of the changed scripts. The setup build (`build.ps1`) is the maintainer's manual check: say so in the batch result and do not run it.
- **Plan v2:** B033.
- **Related:** plan claim C12; BUILD-006, BUILD-011 (same batch).

### BUILD-007: the UWP bridge export check is an ASCII search over the DLL bytes

- **Severity:** medium.
- **Where:** `eng/build-uwp-bridge.ps1` (`if ($Validate)` block: `[Text.Encoding]::ASCII.GetString` over the DLL, then `-notmatch` each name); `eng/build-steam-input-lease.ps1` (the vswhere, `VsDevCmd.bat` and `dumpbin /exports` block that inspects `steam_input_gate.dll`).
- **Problem:** the four entry points the launcher calls by name (`InitializeBridge`, `InitializeInputBridge`, `InputBridgeRoutes`, `BridgeInitialOwnerRequested`) count as "exported" if the string appears anywhere in the image: an import, a log string or debug data. A DLL that lost an export passes `-Validate` and then fails inside a game.
- **Best solution:** add `Get-DllExports -Path <dll>` to `eng/build-common.ps1`. Lift it from `build-steam-input-lease.ps1`: locate VS with vswhere (`-requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64`), run `VsDevCmd.bat -no_logo -arch=x64 -host_arch=x64 >nul && dumpbin.exe /nologo /exports "<dll>"` through `$env:ComSpec`, throw on a non-zero exit, and return the text. `build-steam-input-lease.ps1` keeps its ordinal regexes, applied to that text. `build-uwp-bridge.ps1` dot-sources the helper and requires each name to match `(?m)^\s*\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+<name>(?:\s|$)`. MSVC is already a hard requirement of both scripts, so this adds no dependency and no PE parser.
- **Tests:** `.\eng\build-uwp-bridge.ps1 -Validate` and `.\eng\build-steam-input-lease.ps1 -Validate` locally. Both are non-invasive builds.
- **Plan v2:** B036.
- **Related:** A02-F020; BUILD-008 (same batch, same helper file).

### BUILD-013: `check-steam-module-discovery.mjs` asserts C# call order with regexes over source text

- **Severity:** medium (the verifier corrected the wording: a rename fails loudly with a TypeError; a silent pass happens only if the old method survives beside a new launch path).
- **Where:** `eng/check-steam-module-discovery.mjs` (the `coldStart` slice of `src/WSGM/Core/Steam.cs` `ColdStart`, the `desktopStart` slice of `src/WSGM/Shell/SessionModes.cs` `EnsureSteamDesktop`, the `dlSortVersion` read of `SteamDownloadSort.cs` `ScriptVersion`, and the `resident(file)` extraction of the C# raw-string `ResidentSetup`); the import of `external/steam-ui-toolkit/eng/check-harness.mjs`; `package.json` runs it in every verify.
- **Problem:** B115 splits `Steam` into `SteamInstallation`, `SteamSessionControl` and `BigPictureShortcuts`, and B139 moves download sort into `Source/download-sort.ts`. Both break these regexes. If the old text survived beside the new code, the check would assert on dead code. Asserting C# call order by regex is fragile in any case.
- **Best solution:**
  - Replace the two C# assertions with `SteamLaunchOrderingTests` in `tests/WSGM.Tests`, over `SteamSessionControl` (the instance port from B115). The test needs the three cold-start effects observable in order: the shim reconcile, the CEF remote-debugging flag write and the process start. Use whatever constructor seams B115 gave `SteamSessionControl` for them; for any of the three that B115 left as a direct static call, add one constructor delegate in the shape B115 used for the launcher, with production passing today's static method. Add nothing else.
  - Assert that a cold start calls the reconcile with `"steam-cold-start"`, then the flag write with `(installDirectory, cefEnabled)`, then the start. Assert that `SessionModes.EnsureSteamDesktop` passes `SteamLaunchUnelevated` and `Cef.Enabled` from the config it was given, through the Steam hook B115 put in the `SessionModes` constructor (SESSION-063).
  - Delete the `coldStart` and `desktopStart` blocks from the `.mjs`.
  - Keep the JS module-resolver checks. The download-sort body and its version are then read from the composed asset through B047's fragment harness (`fragment(asset, path)` for the `download-sort.ts` fragment that B139 creates), not from C# raw strings. Take the harness import from wherever B047 exports it.
  - The `resident("SteamLibraryTabs.cs")` extraction stays: no batch moves library tabs out of a C# raw string, and the script is not in the composed asset. A rename there fails loudly (`match(...)[1]` on `null`), which is acceptable.
- **Tests:** `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamLaunchOrdering"`; `npm run steam-assets:check`; `npm run steam-assets:claims`.
- **Plan v2:** B174 (after B139, B141 and B115).
- **Related:** SESSION-056 (B115), STEAMHOST-018 (B139), TOOLKITJS-010 and B047 (fragment markers); BUILD-016.

### BUILD-014: plugin package rules live in four places and the packer admits what the runtime refuses

- **Severity:** medium.
- **Where:** `src/WSGM/Core/PluginPackageFile.cs` (entry, file and byte constants; `IsManagedImage`); `src/WSGM.DeviceLab/Packaging/PluginPackageWorkflow.cs` (byte-identical constants); `eng/package-plugin.ps1` (`$files.Count -gt 4096` and the `PEReader` native-image loop); `eng/pack-device.ps1`; host-provided names in `src/WSGM/Shell/PluginPackageLoader.cs` (`PluginLoadContext.HostOwned`) and `eng/plugin-package-common.ps1` (`Remove-HostProvidedFiles` `$hostProvided`); `eng/plugin-manifest.cs`.
- **Problem:** the packer allows 4096 entries while the runtime allows 1024 entries and 512 files, so a package can build cleanly and then fail at install. The native-image refusal exists twice (packer and `PluginPackageFile.IsManagedImage`), and so does the host-provided assembly list. One side can change without the other.
- **Best solution:** one owner, in the SDK, as part of the plan v2 B147 sdk batch:
  - New `WSGM.Device.Sdk.Packaging.PluginPackageLayout` (MIT contract). It holds the two byte bounds D2 keeps (128 MiB per file, 512 MiB total: they refuse oversized input and never truncate it, as zip-bomb bounds), `IsManagedImage(Stream)` (moved from `PluginPackageFile`, using the in-box `PEReader`), and `HostProvidedAssemblies`: `WSGM.Device.Sdk`, `WSGM.Plugin.Sdk`, `SteamUiToolkit`, `WinRT.Runtime`, `Microsoft.Windows.SDK.NET`.
  - `MaxPackageEntries`, `MaxPackageFiles` and the packer's 4096 count go: no count caps.
  - `PluginPackageFile` (its private `IsManagedImage(byte[])`, called through a `MemoryStream`) and `PluginPackageWorkflow` (its private `IsManagedPe`) use the layout. Delete their constants and their own PE checks. `PluginPackageFile.IsX64ManagedAssembly` stays where it is.
  - Make `PluginLoadContext.HostOwned` `internal` (WSGM already grants `InternalsVisibleTo("WSGM.Tests")`) and add a test that its keys equal `HostProvidedAssemblies`.
  - `eng/plugin-manifest.cs` gains `validate-package <file>` (open the zip, apply the layout's byte bounds and `IsManagedImage` to every `.dll`, `.exe` and `.sys` entry, then validate the manifest with `PluginManifestReader.TryRead`) and `host-provided` (print one name per line). It reaches the Device SDK through the Plugin SDK's project reference. `validate-package` is for common packages only: device packages have no `category` and are read by `DeviceManifestReader`, and `pack-device.ps1` already validates them through `wsgm-device validate`, which uses `PluginPackageWorkflow` and so the same layout.
  - `package-plugin.ps1` keeps publish, strip, stamp and zip, then calls `validate-package` on the staged archive, which now carries the native-image refusal. Delete the PowerShell `4096` count and the PE loop. Keep the reparse-point refusal that shares the count's `if` (`ZipFile.CreateFromDirectory` would follow a link into the archive); it is a type check, not a cap.
  - `Remove-HostProvidedFiles` reads its names from `host-provided`.
  - Keep `File.ReadAllBytes` plus `TryRead`. A02_01 step 3's share-mode and growth ceremony stays dropped.
- **Tests:** `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Packaging"` (layout, managed-image check); `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginPackage|FullyQualifiedName~PluginCatalog|FullyQualifiedName~CommonPluginEnablement"` (with the `HostOwned` equality test); a local `eng/package-plugin.ps1` run on `src/WSGM.Plugin.Ir` into a temp path, plus one with a native DLL dropped into the payload, which must refuse.
- **Plan v2:** B147, decided: D2 accepts exactly the plan v2 byte-bound list, which includes these two package bounds; every count cap goes.
- **Related:** A02-F021, A02-C02, SDK-008, SDK-012, SDK-V-003; BUILD-027 (no change).

### BUILD-018: the `new-plugin.ps1` templates are uncompiled C# in here-strings

- **Severity:** medium (the verifier corrected the recommendation: as written it would not build green).
- **Where:** `eng/new-plugin.ps1` (here-strings `$source` for the common plugin and `$gpuSource` for the GPU plugin; manifest `entryType = 'ExamplePlugin.Plugin'`); `tests/WSGM.Plugin.Sdk.Tests/WSGM.Plugin.Sdk.Tests.csproj`; `docs/plugin-system.md` (scaffold section); `.gitattributes` (no rule for `eng/**/*.cs`; `eng/plugin-manifest.cs` is LF in the index).
- **Problem:** the templates use `IPlugin`, `IPluginActions`, `IPluginUi`, `ICapabilityPlugin`, `CapabilityCommandResult` and `ApplicationProfileSyncResult`, but no build or test compiles them. Plugin API 4 and Device 12 will break both templates while every gate stays green. The Device Lab scaffold has a build test; the common scaffold has none.
- **Best solution:**
  - Move the two sources to `eng/templates/CommonPlugin/Plugin.cs` (namespace `ExamplePlugin.Common`) and `eng/templates/GpuPlugin/Plugin.cs` (namespace `ExamplePlugin.Gpu`). Each is valid C# whose plugin id is the string literal `"__PLUGIN_ID__"`.
  - Restyle both to repository layout: expanded braces (the braceless `if (request.ActionId != "increment")` in the common template's `ExecuteActionAsync` trips `csharp_prefer_braces = true:warning` under `EnforceCodeStyleInBuild`), and no one-line block bodies such as `DisposeAsync() { _host = null; return ...; }` in both templates. Then `--warnaserror` and `dotnet format --verify-no-changes` both pass. The generated text changes, harmlessly.
  - In `WSGM.Plugin.Sdk.Tests.csproj`, add `<Compile Include="../../eng/templates/**/*.cs" Link="Templates/%(RecursiveDir)%(Filename)%(Extension)"/>`. The test project already has `ImplicitUsings` and references the Plugin SDK.
  - `new-plugin.ps1` reads the chosen file, replaces `__PLUGIN_ID__`, and writes `Plugin.cs`. Its manifest `entryType` becomes `ExamplePlugin.Common.Plugin` or `ExamplePlugin.Gpu.Plugin`.
  - Update the scaffold section of `docs/plugin-system.md`.
  - Add `eng/**/*.cs text eol=crlf` to `.gitattributes`, beside the `src/**/*.cs` rule, so the compiled templates check out CRLF on every OS like the rest of the C# family. No renormalize is needed: `git ls-files --eol eng/plugin-manifest.cs` already shows `i/lf w/crlf`, which is what the rule produces.
  - This must land before B052 (Plugin API 4) and B142 (Device 12), so those batches cannot finish with broken templates.
- **Tests:** `dotnet build tests/WSGM.Plugin.Sdk.Tests/WSGM.Plugin.Sdk.Tests.csproj -c Release --warnaserror`. Also run `eng/new-plugin.ps1` once for each category into a temp folder, then `dotnet build` the output; this is a read-only scaffold.
- **Plan v2:** B035.
- **Related:** plan claim C17 (corrected: `PluginManifestFixture` follows `DeviceApi.Version`; the literal pin is in `ContractBoundaryTests`).

### BUILD-021: boundary tests guard one edge of a much larger project graph

- **Severity:** medium.
- **Where:** `tests/WSGM.Tests/Boundaries/DeviceBoundaryTests.cs` (`WsgmLoadsThePluginDynamicallyWithoutReferencingItsProject`, `SolutionBuildsTheDeviceProjectsWithOneSharedSdk`, `DeviceSdkHasNoProjectOrPackageDependencies`); `tests/Shared/RepositoryFiles.cs` (`Root`, `LoadProject`).
- **Problem:** nothing asserts the edges this refactor moves:
  - WDC and the toolkit reference no first-party project.
  - Plugins reference only the SDKs, plus WDC for NVIDIA.
  - `WSGM.Launch`, `WSGM.LogonService` and `WSGM.PackagedLaunch` have no `ProjectReference`.
  - Linked `Compile Include` items come only from the known shared files and the steam-input-lease binding.
  - The Plugin SDK holds exactly two references.

  A wrong edge added during a move would compile and pass every test.
- **Best solution:** add `tests/WSGM.Tests/Boundaries/ProjectGraphTests.cs` and delete `DeviceBoundaryTests.cs`. `ContractBoundaryTests` (in `tests/WSGM.Device.Sdk.Tests/Boundaries`) stays: it holds no dependency assertion.
  - Two static tables keyed by repo-relative csproj path, covering every project in `WSGM.slnx` (production, tests, WDC, toolkit):
    - `ProjectReferences`: the exact set of referenced csproj paths, normalized to repo-relative forward-slash paths.
    - `LinkedSources`: the exact set of `Compile Include` values that resolve outside the project's own directory, normalized the same way, with globs compared as literal strings.
  - Four facts:
    - Every slnx project has a row, and every row is in the slnx.
    - Each project's references equal its row.
    - Each project's linked sources equal its row.
    - `WSGM.Device.Sdk` has no `PackageReference`.
  - The rows record today's graph (linked sources at their current paths), so the test passes now. Every later move edits one row in the same batch.
  - Exact equality, not "allowed subset", also catches additions. Equality on the WSGM row covers "WSGM references no device package", and equality on every row covers "one shared SDK path". Both old tests become redundant.
- **Tests:** `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Boundaries"`.
- **Plan v2:** B034. Later rows change in B173.
- **Related:** BUILD-029 (the tripwire stays), BUILD-019.

### BUILD-002: `verify.ps1` cannot tell Rider's cleanup changes from the worker's own edits

- **Severity:** low (the verifier lowered it from medium: this is workflow friction on an uncommitted tree, not a defect in the committed-head gate).
- **Where:** `eng/verify.ps1` (`dotnet jb cleanupcode ... --include="src\**\*.cs;tests\**\*.cs"` followed by `if (-not $Fix) { git diff --exit-code --stat -- src tests }`).
- **Problem:** the check fails on any unstaged change under `src` or `tests`, including csproj, axaml and manifest files that jb never touches. So it only works on a clean tree, and it cannot say whether jb changed anything. It also causes BUILD-V-003.
- **Best solution:** compare what jb actually touched.
  - Before `jb cleanupcode`, record `Get-FileHash` for every file jb will process: `Get-ChildItem src, tests -Recurse -Filter *.cs`, excluding `\bin\`, `\obj\` and `src\WSGM\ThirdParty\`. Keep a path-to-hash dictionary.
  - After jb, hash the same files again. Without `-Fix`, throw `"Rider's Full Cleanup changed: <paths>; review them or run eng/verify.ps1 -Fix"` if any hash differs.
  - Delete the `git diff --exit-code` call.
  - This covers tracked, staged, unstaged and untracked C# alike and ignores non-C# edits. It adds no state.
  - jb still rewrites in place in check mode. That is acceptable, and the message names the files.
- **Tests:** a PowerShell parse of `verify.ps1`. The behaviour is proven at B179 on the committed head. Before that, the maintainer may optionally run verify once on a tree with an unrelated csproj edit; it must not fail on that edit.
- **Plan v2:** B032.
- **Related:** BUILD-V-002, BUILD-V-003.

### BUILD-006: the controller asset names are hard-coded beside the lock file

- **Severity:** low (the verifier lowered it from medium: a HidHide bump fails loudly in `build.ps1` at copy time, and a usbip bump is already caught by `assert-controller-pin.ps1`. Nothing wrong can ship).
- **Where:** `build.ps1` (the `Payload\Controller` copy list names `USBip-0.9.8.1-x64.exe` and `HidHide_1.5.230_x64.exe`); `eng/assert-component-staging.ps1` (`Require-File "Controller\USBip-0.9.8.1-x64.exe"` and the HidHide line); `eng/assert-controller-pin.ps1` (the `$build -notmatch [regex]::Escape($entry.asset)` check over `build.ps1` text); `external/controller/controller-components.lock.json` (`components[].asset` for `usbip-win2` and `hidhide`).
- **Problem:** a pin bump in the lock passes every check and fails late, inside `build.ps1`. Three files restate what the lock already says.
- **Best solution:**
  - In `build.ps1`, read `$controllerAssets = @((Get-Content -LiteralPath "$root\external\controller\controller-components.lock.json" -Raw | ConvertFrom-Json).components.asset)`, the same source `acquire-controller-dependencies.ps1` already uses. The copy list becomes `libviiper.dll`, `libviiper.h`, `VIIPER-LICENSE.txt`, `VIIPER-NOTICE.md` plus `$controllerAssets`.
  - `assert-component-staging.ps1` reads the lock the same way and runs `Require-File "Controller\$asset"` for each entry.
  - In `assert-controller-pin.ps1`, delete the `build.ps1` text check (the `$buildPath`/`$build -notmatch` block) and drop "and the installer's [Files] entry" from the comment above the staged-path check: there is no Inno installer, and only `Install-UsbipDriver.ps1` still names the asset.
  - Setup itself is unaffected (`SetupEngine` globs `HidHide*.exe`).
- **Tests:** `.\eng\assert-controller-pin.ps1`; a PowerShell parse of the changed scripts.
- **Plan v2:** B033.
- **Related:** BUILD-005.

### BUILD-008: three implementations of "download a pinned asset and verify digest and signer"

- **Severity:** low.
- **Where:** `eng/acquire-controller-dependencies.ps1` (loop over `$selected`: `Invoke-WebRequest` straight to the destination, no cache, no retry); `eng/acquire-pawnio.ps1` (`Test-Pinned`, `Get-Pinned` with three attempts, and `.partial` then `Move-Item`); `eng/stage-webview2-runtime.ps1` (cache under `artifacts\webview2`, no `.partial`, and a signer check by subject regex `CN=Microsoft Corporation`).
- **Problem:** three copies with different safety properties. The controller script can leave a partially written asset at the final path when the hash check never runs, for example when the process is killed mid-download.
- **Best solution:** add `Get-PinnedAsset -Url -Path -Sha256 [-SignerThumbprint] [-SignerSubjectPattern]` to `eng/build-common.ps1`.
  - If `-Path` exists and its SHA-256 matches, verify the signer and return.
  - Otherwise download to `<Path>.partial`, keeping PawnIO's existing three-attempt loop (existing behaviour, not a new mechanism), verify SHA-256 and the signer on the partial file, and `Move-Item -Force` it to `-Path`.
  - On any failure, delete the partial file and throw.
  - Each lock keeps its current signer rule: a thumbprint for controller and PawnIO, the subject pattern for WebView2.
  - The three scripts call the helper. PawnIO's module-archive extraction stays in `acquire-pawnio.ps1`, but the archive download itself uses the helper.
- **Tests:** `.\eng\assert-pawnio-pin.ps1`. Run each acquire script once into a temp destination; this is a download only, nothing installs.
- **Plan v2:** B036.
- **Related:** BUILD-007.

### BUILD-010: the live-data path scan misses the `%VAR%` and PowerShell forms

- **Severity:** low.
- **Where:** `eng/check-no-live-data-paths.ps1` (`$patterns`: `LOCALAPPDATA[\\/]+WSGM`, `SpecialFolder\.LocalApplicationData`, `GetEnvironmentVariable\(\s*"LOCALAPPDATA"`); `tools/PerfLab/perf-capture.ps1` (`[string] $OutRoot = (Join-Path $env:LOCALAPPDATA 'WSGM\perf')` and the help text naming `%LOCALAPPDATA%\WSGM\perf`).
- **Problem:** `%LOCALAPPDATA%\WSGM` does not match the first pattern, because the `%` sits between the variable name and the separator. `$env:LOCALAPPDATA` is not matched at all. `perf-capture.ps1` writes a trace output of up to 1 GB into the live WSGM data folder and passes the scan.
- **Best solution:**
  - Add two patterns: `@{ Name = "literal %LOCALAPPDATA%\WSGM path"; Regex = '%LOCALAPPDATA%[\\/]+WSGM' }` and `@{ Name = "PowerShell LOCALAPPDATA"; Regex = '\$env:LOCALAPPDATA\b' }`.
  - In `perf-capture.ps1`, make the `$OutRoot` default `''` and, right after the existing `$repoRoot = Resolve-Path ...` line, add `if (-not $OutRoot) { $OutRoot = Join-Path $repoRoot 'artifacts\perf' }` (`artifacts/` is gitignored). Resolve it in the body, not in the parameter default: the script relaunches itself through `(Get-Process -Id $PID).Path`, which can be Windows PowerShell, where `$PSScriptRoot` is empty while defaults bind (the same reason `assert-controller-pin.ps1` gives). The elevated relaunch already passes the resolved `-OutRoot`. Fix the help text and the output path in `tools/PerfLab/README.md`.
  - Reword the four existing comment lines the new patterns would match so they name "the user's WSGM data folder": `tests/WSGM.Tests/Core/SteamInputShimTests.cs` (doc comment), `tests/WSGM.Tests/Settings/SettingsViewModelSplashTests.cs` (two comments) and `src/WSGM.DeviceLab/Capture/Live/LabWmiFirmwareEvents.cs` (one comment). Do not add allow markers to comments.
  - Do not grow the scanner further. B037's injected `UserDataContext` is the structural fix for indirect reach through statics.
- **Tests:** `.\eng\check-no-live-data-paths.ps1` must pass after the change and fail if one reworded line is temporarily reverted. Also a PowerShell parse.
- **Plan v2:** B032.
- **Related:** B037 (UserDataContext).

### BUILD-011: `build-steam-input-lease.ps1` documentation and staging disagree with the app

- **Severity:** low.
- **Where:** `eng/build-steam-input-lease.ps1` (`.PARAMETER Validate` says the release build skips `-Validate`; the staging loop copies `steam-input-lease.exe` under the comment "All three must ship together"); `build.ps1` (`& "$root\eng\build-steam-input-lease.ps1" -Validate`); `src/WSGM/WSGM.csproj` (comment: `steam-input-lease.exe` is deliberately not shipped; only `Native\SteamInputLease\*.dll`, `*.txt` and `*.md` are copied).
- **Problem:** the script stages a binary nothing ships, and both comments are wrong, which misleads anyone changing the lease pipeline.
- **Best solution:**
  - Remove `"steam-input-lease.exe"` from the staging `foreach`.
  - Rewrite the comment above it: the gate is injected into steam.exe, and the FFI library is what the managed binding loads. The CLI is not shipped, because `WSGM.Launch.exe` is the launch wrapper.
  - Rewrite `.PARAMETER Validate` to say that `eng\verify.ps1` and `build.ps1` both pass it.
- **Tests:** a PowerShell parse. `.\eng\build-steam-input-lease.ps1 -Validate` locally (a non-invasive build).
- **Plan v2:** B033.
- **Related:** none.

### BUILD-012: WDC builds under WSGM's build configuration only inside this checkout

- **Severity:** low.
- **Where:** `external/windows-device-control` has no `Directory.Build.props` or `.editorconfig`, so in this checkout it inherits WSGM's `Directory.Build.props` (`EnforceCodeStyleInBuild`, `Nullable`, `NuGetAudit=false`, `AVLN3001`) and WSGM's root `.editorconfig`. `eng/verify.ps1` builds it with `--warnaserror` but excludes `external/` from `dotnet format`.
- **Problem:** WDC's diagnostics depend on where it is checked out. Standalone it builds with neither file, and once it gets its own roots the parent build sees a different warning set.
- **Best solution:** child side only (B046), in the shape `external/steam-ui-toolkit` already has: an explicit-root `external/windows-device-control/Directory.Build.props` (no import of the parent) restating `EnforceCodeStyleInBuild`, `Nullable`, documentation generation and `NuGetAudit=false`, so in-tree diagnostics stay the same, plus a child `.editorconfig` that starts with `root = true` and holds only the rules the library already meets. Without `root = true` the parent's `.editorconfig` would still apply in this checkout and not standalone, which is the defect. Child commit and push first, then the parent gitlink. Nothing changes in `verify.ps1`. If the parent `--warnaserror` build reports a new WDC warning after the gitlink, fix it in the child, never by formatting the child from the parent.
- **Tests:** the full WDC suite on net8 and net10 (B046), and the parent `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true` warning-free.
- **Plan v2:** B046.
- **Related:** U01-045, U01-067, U01-068; plan claim C7.

### BUILD-015: the Steam asset builder carries dead plugin discovery and a size cap that was raised twice

- **Severity:** low.
- **Where:** `eng/build-steam-assets.mjs` (`discoverPluginSourceDirectories`, `pluginSourcePaths` and their slot in `sourcePaths`; the header comment "there are none today"; `const maximumAssetBytes = 768 * 1024` and the `bytes.length > maximumAssetBytes` check); `docs/steam-cef-system.md` (the 768 KiB statement).
- **Problem:** no `src/WSGM.Plugin.*/SteamUiAssets` exists. If one did, the builder would compile plugin UI into the WSGM core asset rather than into the package. The header comment is false: nine WSGM fragments exist under `Source`. The size cap is, by its own comment, "not a limit anything downstream imposes", and it has been raised twice. It is an arbitrary limit.
- **Best solution:**
  - Delete `discoverPluginSourceDirectories`, `pluginSourceDirectories` and `pluginSourcePaths` and their spread in `sourcePaths`.
  - Fix the header comment: WSGM's own fragments under `Source/` are appended after the toolkit's.
  - Delete `maximumAssetBytes` and its comment. The bytes check keeps only "non-empty, no BOM, valid UTF-8", and the single-file check stays.
  - Remove the 768 KiB sentence from `docs/steam-cef-system.md`.
- **Tests:** `npm run steam-assets:check`; `npm run steam-assets:claims`.
- **Plan v2:** B032.
- **Related:** STEAMHOST-V-003, TOOLKITJS-037, TOOLKITJS-010; BUILD-V-001 and BUILD-C-001 (same file, same batch; BUILD-C-001 removes the 1 MiB `spawnSync` default that would otherwise become the new cap).

### BUILD-016: live tools scrape C# source and hard-code Steam module ids

- **Severity:** low.
- **Where:**
  - `tools/WsgmLibTest/run-prod-sort.mjs` (regex over `SteamDownloadSort.cs` `ResidentSetup` and `SteamUiBridgeIdentity.Namespace`).
  - `tools/WsgmLibTest/qam-harness.mjs` (regex over `Surfaces/*.cs` `PatchId` and `Commands`, and `SteamUiSessionHost.cs`).
  - `tools/WsgmLibTest/probe-*.js` (nine files that sweep the Steam module registry, for example `runtime.m["79476"]`, an id the September 2026 client renumbered).
  - `tools/WsgmLibTest/cdp.mjs` (`http://localhost:${PORT}/json`).
- **Problem:** B139 (download sort to TS, `wsgm.*` patch ids), B053 and the Steam host refactor break the scrapers without any error. The probes sweep the module registry and use hard-coded ids, which the CLAUDE.md Steam CEF rule forbids ("never sweep the module registry"). `localhost` can resolve to `::1`, while the plan pins the loopback connection to `127.0.0.1`.
- **Best solution:**
  - `run-prod-sort.mjs` evaluates the download-sort fragment taken from the shipped `src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js` by its emitted fragment marker (B047). It no longer reads C#.
  - `qam-harness.mjs` reads patch ids and command names from the shipped asset, or from the C#-emitted fixture B139 adds for toolkit tests, instead of regexes over `Surfaces/*.cs`.
  - `cdp.mjs` uses `http://127.0.0.1:${PORT}/json`.
  - Delete the nine `probe-*.js` files. `eng/check-steam-fingerprints.mjs` is the offline replacement. The only tracked references are in the Steam CEF debugging skill: `.agents/skills/wsgm-steam-cef-debugging/references/live-tools.md` (the `probe-*.js` mutating-section list and the `probe-register.js --section token-exists` advice) and the `probe-` sentence in `.agents/skills/wsgm-steam-cef-debugging/SKILL.md`. Remove those lines in the same commit, shown as its own guidance diff with the batch and applied, as D4 approved (`git grep -n "probe-" -- .agents docs tools` must then name no deleted file).
  - Do not run these tools: they mutate live Steam and are attended.
- **Tests:** `node --check` on each changed `.mjs`; `npm run steam-assets:check`. A live run is the maintainer's attended check only.
- **Plan v2:** B174 (plan v2 resolved BUILD Q4 as "retire the probes").
- **Related:** BUILD-013; B139, B047, B053.

### BUILD-017: `InternalsVisibleTo("DeckSpike")` opens the product to a tool nothing compiles

- **Severity:** low.
- **Where:** `src/WSGM/Properties/AssemblyInfo.cs` (`[assembly: InternalsVisibleTo("DeckSpike")]`); `tools/DeckSpike`.
- **Problem:** the GPL product's internals are opened to an out-of-solution bench tool whose experiment (2026-09-28) has already been answered. Nothing compiles the tool, so the grant protects nothing.
- **Best solution:** D12 is decided: delete. Remove `tools/DeckSpike` and `tools/SteamReceiver` (both directories, including their READMEs) and the `InternalsVisibleTo("DeckSpike")` line with its two comment lines. The only other tracked reference is `docs/device-integration.md` (around line 395): delete the sentence that introduces `tools\DeckSpike` and `tools\SteamReceiver`, and keep the dated "Bench result of 2026-09-28" paragraph after it as evidence, changing its first "the receiver" to "a bench receiver (since removed)". Confirm with `git grep -n "DeckSpike\|SteamReceiver"`.
- **Tests:** `dotnet build src/WSGM/WSGM.csproj -c Release`; the tools step from BUILD-003.
- **Plan v2:** B174, decided: D12, delete both tools and the `InternalsVisibleTo`.
- **Related:** BUILD-003, BUILD-V-005.

### BUILD-019: linked source files live inside their owner projects

- **Severity:** low.
- **Where:** linked `Compile Include` items in `src/WSGM.Launch/WSGM.Launch.csproj`, `src/WSGM.LogonService/WSGM.LogonService.csproj`, `src/WSGM.PackagedLaunch/WSGM.PackagedLaunch.csproj` and `src/WSGM.DeviceLab/WSGM.DeviceLab.csproj`, plus `tests/WSGM.Tests/WSGM.Tests.csproj` (links `src/WSGM.LogonService/LogonDecision.cs`); `src/WSGM.Install/InstallLayout.cs` is compiled by its own project and linked into the logon service; `.editorconfig` blocks such as `[{src/WSGM/Core/AtomicFile.cs,src/WSGM/Core/BootManifest.cs}]`; `src/Shared/Gpu` (the one proper shared home today).
- **Problem:** an edit to `src/WSGM/Core/AtomicFile.cs` or `src/WSGM.Launch/RotatingFileLog.cs` silently changes the logon service. The "no Log, no ConfigStore, explicit usings" rule is visible only in file comments and `.editorconfig` blocks.
- **Best solution:**
  - Move every file that is still shared after the session and install batches into `src/Shared/<area>`. Change no symbol, namespace or behaviour. Each file's header names its consumers and the dependency-light rule.
  - `src/WSGM` compiles the files it uses through explicit `<Compile Include="..\Shared\...">`, as the launchers do. The default glob no longer sees them, because the physical files leave `src/WSGM`.
  - Target homes (from the review's section 4.2, adjusted by plan v2):
    - `Process`: `ScheduledTaskXml` (still shared: both elevated callers keep schtasks, and B029's admin-only staging folder is dropped as security hardening), `WindowsCommandLine`, `Win32Common`, `ParentProcessStart`, `SteamControllerExclusion` and `RotatingFileLog`. `SessionProtocolNames` is already created there by B031.
    - `Boot`: `BootManifest` and `AtomicFile`.
    - `Install`: `InstallLayout` (`WSGM.Install.csproj` then compiles it through an explicit `<Compile Include="..\Shared\Install\InstallLayout.cs">`, like the logon service).
    - `Launch`: `PackagedLaunchCommand`.
    - `Interop`: `Kernel32`, `NativePathIdentity` and `NativeHidHide`, after B172 merges `NativeHidHide.Paths`. Per D3 they keep their GPL headers: no relicensing and no duplicate copy. Device Lab, which links all three, becomes GPL instead; its `LICENSE`, csproj licence metadata, README, AGENTS and notices change in the same batch under LABCORE-039 and A02-F022. The SDKs stay MIT.
  - `NativePackageSource` is not shared. It moves physically into `src/WSGM.DeviceLab`, its only consumer, and WSGM stops compiling it (critic conflict 22, LIBRARY-025).
  - The INSTALL-032 launcher-shared sources go under `src/Shared/Process`.
  - The `.editorconfig` blocks follow the files.
  - Each `ProjectGraphTests` row changes in the same batch.
  - Add no new assembly and no props indirection.
- **Tests:** `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true`; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Boundaries|FullyQualifiedName~Launch|FullyQualifiedName~Logon|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~BootManifest|FullyQualifiedName~AtomicFile"`; `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~Package"`.
- **Plan v2:** B173 (after B172, B141, B031 and what remains of B029), decided: D3, Device Lab becomes GPL and the interop files are neither relicensed nor duplicated. The AGENTS and README path mentions (`src/WSGM.PackagedLaunch/AGENTS.md`, `src/WSGM.DeviceLab/README.md`) are shown as their own diff with the batch and applied (D4).
- **Related:** A02-C05, A02-F022, LABCORE-039, LIBRARY-025, INSTALL-032; BUILD-020 (no merge), BUILD-023.

### BUILD-025: shared test helpers are partly single-consumer and misnamed

- **Severity:** low.
- **Where:** `tests/Shared/SplashConfigBuilder.cs` (linked only by `tests/WSGM.Tests/WSGM.Tests.csproj`); `tests/Shared/TemporaryDirectory.cs` (root folder name `"wsgm-device-tests"` for every suite); `tests/WSGM.UiTests/WSGM.UiTests.csproj` (links `../WSGM.Tests/Fakes/FakeButtonSource.cs` and `../WSGM.Tests/Fakes/FakeHybridCoreApi.cs`).
- **Problem:** the tests guide says shared code lives in `tests/Shared`. A single-consumer helper sits there, while fakes that two suites share do not. The temp root name suggests every suite is a device test, which misleads anyone cleaning up stray directories.
- **Best solution:**
  - Move `SplashConfigBuilder.cs` into `tests/WSGM.Tests/Builders/` and drop its `Compile Include` link.
  - Rename the temp root to `"wsgm-tests"`.
  - Move `FakeHybridCoreApi.cs` to `tests/Shared` with the MIT header, and point both csproj files at it. `FakeButtonSource` moves in B172 (input), which runs earlier: do not move it twice.
- **Tests:** `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Splash"`; `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings"`.
- **Plan v2:** B176.
- **Related:** INPUT-032 and B172 (`FakeButtonSource`); BUILD-036.

### BUILD-026: wall-clock polling is the default async test idiom

- **Severity:** low.
- **Where:** `tests/Shared/AsyncConditions.cs` (`WaitForAsync` polls every 10 ms for up to 10 s); about 39 call sites in 10 files, 19 of them in `SteamUiSessionHostTests`.
- **Problem:** these tests assert eventual state, not order or call counts, and they stretch under load. New lifecycle tests would copy the idiom.
- **Best solution:** leave the helper and the existing call sites alone. Add one paragraph to `tests/WSGM.Tests/AGENTS.md` (its own diff, shown with the batch and applied per D4): a new test of an owner's lifecycle awaits the task the owner exposes (its `StopAsync`, a tracked work task, or a `TaskCompletionSource` signalled by a fake) or advances a fake clock. `AsyncConditions.WaitForAsync` is for observing eventual state only. Replace a polling call only when the batch that touches its owner exposes a completion to await. Run `.\eng\check-agent-guidance.ps1` afterwards.
- **Tests:** `.\eng\check-agent-guidance.ps1`; `npm run format:check`.
- **Plan v2:** B176.
- **Related:** none.

### BUILD-028: an eng helper is tested from the Device Lab suite by spawning pwsh

- **Severity:** low.
- **Where:** `tests/WSGM.DeviceLab.Tests/Eng/DevicePackageOutputTests.cs` (launches PowerShell to test `Publish-DevicePackageArchive` and locates the repository with the production `DeviceLabRepositoryLocator.Find`); `eng/plugin-package-common.ps1` (`Publish-DevicePackageArchive`).
- **Problem:** a product test suite tests `File.Replace` and `File.Move` semantics of a 10-line build helper through a child process, and it depends on product code to find the repository.
- **Best solution:** with BUILD-030's rule (temporary work in a fresh temp directory, outputs written by moving a finished file into place), `Publish-DevicePackageArchive` is one line in each caller, so inline it and delete the helper:
  - `pack-device.ps1`: `Move-Item -LiteralPath $stagedArchive -Destination $archive -Force`. It replaces only the same-named `<id>-<version>.wsgmpkg`, as `-ReplaceExisting` does today.
  - `package-plugin.ps1`: `Move-Item -LiteralPath $stagedArchive -Destination $archivePath` without `-Force`, which keeps today's create-new refusal (the script already throws "Archive already exists" up front).
  - Delete `DevicePackageOutputTests.cs` and the then-empty `tests/WSGM.DeviceLab.Tests/Eng` folder.
- **Tests:** a PowerShell parse; `.\eng\pack-device.ps1 -Source src/WSGM.Device.Msi.Claw -RequireGlyphs` locally (offline, no hardware); `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~Packaging"`.
- **Plan v2:** B175, decided: D4, the staging-rule guidance diffs are approved and applied with the batch.
- **Related:** BUILD-030, BUILD-036.

### BUILD-030: four different "owned staging" implementations

- **Severity:** low.
- **Where:**
  - `eng/build-bundle.ps1` (temp directory plus PID plus marker file).
  - `eng/pack-device.ps1` (work marker plus a per-archive marker file beside the output).
  - `eng/package-plugin.ps1` (create-new stage directory `.wsgm-plugin-*`, plus the prefix and parent check in `finally`).
  - `eng/publish-device-lab.ps1` (marker, ancestor reparse walk, backup, restore, and reparse re-checks before and after).
  - The rules come from `eng/AGENTS.md` (build and staging rules) and `src/WSGM.DeviceLab/AGENTS.md`.
- **Problem:** about 250 lines of ceremony protect outputs that live under the gitignored `publish/` or a temp directory. `build.ps1` already clears and recreates `publish/` with no ceremony.
- **Not the solution (checked 2026-10-03):** the earlier "clear and recreate any destination under `publish/` or `artifacts/`, refuse everything outside the repository" rule breaks three callers. `build-bundle.ps1` publishes Device Lab into `$temporaryRoot\Tools\DeviceLab` and packs every plugin into `$temporaryRoot\Packed`, both under the system temp folder. `pack-device.ps1` defaults `-OutputRoot` to `publish`, so clearing its output would delete `publish\App` and every other package. `docs/plugin-system.md` documents `package-plugin.ps1 -Archive C:\work\example.counter-0.1.0.wsgmpkg` for third-party authors, a workflow that must keep working.
- **Best solution:** one rule, stated in `eng/AGENTS.md` and `src/WSGM.DeviceLab/AGENTS.md` (each its own diff, shown with B175 and applied per D4): a script deletes only what it created in this run, or the one fixed output directory it owns. Temporary work goes in a fresh directory under `[IO.Path]::GetTempPath()` with a GUID name, removed in `finally` with no marker or path check. A packaged file is written by moving the finished file into place.
  - `publish-device-lab.ps1` is the only script that clears a directory. Add `Reset-OwnedOutput -Path` to `eng/build-common.ps1`: resolve the full path, require it to be strictly below `<repo>\publish\` or `<repo>\artifacts\` and throw otherwise, then remove its contents and recreate it, as `build.ps1` does for `publish\`. Its default `publish/DeviceLab` passes. Delete its marker, ancestor reparse walk, backup, restore and reparse re-checks.
  - `Publish-DeviceLab` in `device-lab-publish.ps1` is unchanged: `build-bundle.ps1` calls it with a fresh temp destination.
  - `pack-device.ps1`: the work root moves from `<OutputRoot>\.wsgm-pack-<guid>` to a fresh temp directory removed in `finally`. Delete the work marker and the per-archive `.wsgm-generated-output` markers. The archive is moved into `-OutputRoot` with `-Force` (BUILD-028). `-OutputRoot` stays unrestricted, because the bundle passes a temp folder.
  - `package-plugin.ps1`: the stage directory moves from `.wsgm-plugin-*` beside the archive to a fresh temp directory removed in `finally`. Delete the prefix and parent check in `finally` and the ancestor reparse-point loop over the archive's parent, which only protected the stage that lived there. Keep the `.wsgmpkg` extension check, the up-front "Archive already exists" refusal and the parent-exists check.
  - `build-bundle.ps1`: `$temporaryRoot` becomes `Join-Path ([IO.Path]::GetTempPath()) ("WSGM-Bundle-" + [Guid]::NewGuid().ToString("N"))`, removed in `finally` with a plain `Remove-Item -Recurse -Force`. Delete the PID, the marker file and the guarded removal. Keep the existing "output must stay inside the repository" check and the "refusing to overwrite existing bundle output" check: neither deletes anything.
  - The two leftovers from BUILD-032 are fixed in the rewritten files: the double dot-source of `plugin-package-common.ps1` in `pack-device.ps1`, and the unindented `try` body in `publish-device-lab.ps1`.
- **Tests:** a PowerShell parse; locally, `.\eng\publish-device-lab.ps1` (default `publish/DeviceLab`) twice in a row, and once with `-OutputRoot C:\Temp\x`, which must refuse; `.\eng\pack-device.ps1 -Source src/WSGM.Device.Msi.Claw -RequireGlyphs` twice in a row (offline), after which `publish\` holds the archive and nothing else it did not hold before; `.\eng\build-bundle.ps1 -OutputRoot publish\bundle-check -SkipTools -SkipCommunity` (offline staging, then delete the folder); `.\eng\package-plugin.ps1` on `src/WSGM.Plugin.Ir` with `-Archive` in a temp folder outside the repository, which must succeed.
- **Plan v2:** B175, decided: D4, the guidance diffs are approved and applied with the batch (review Q3).
- **Related:** BUILD-028, BUILD-032.

### BUILD-034: guidance and docs that are already wrong

- **Severity:** low (the verifier moved the `docs/logging.md` bullet: it is accurate today).
- **Where and fix, one item each:**
  - `AGENTS.md` (root, "How the maintainer works"): "WSGM.slnx.DotSettings carries the inspection overrides". It carries exactly one (`RedundantUnsafeContext`); the other overrides are about 75 `resharper_*_highlighting` entries scoped by path in `.editorconfig`. Reword it: "Rider inspection overrides live in `.editorconfig` (`resharper_*_highlighting`); `WSGM.slnx.DotSettings` is the solution settings layer `jb cleanupcode` reads." Keep the "keep named arguments on literal values" clause.
  - `tests/WSGM.Tests/AGENTS.md`: the scope line lists only the application, launcher and logon service. Add Setup, PackagedLaunch, the IR package host and the toolkit. After B037, its "no named resources" rule is true, so leave that line.
  - `docs/ui.md` (UI test section): the baseline area list omits Steam, Tools, Power, Keyboard, Power menu, Profiles, Graphics and Sections. List all areas that have baselines.
  - `docs/logging.md`: `PluginTrace` stays (the SDK keeps it, critic conflict 18). Add only the common-plugin host trace channel that B052 introduces.
  - `docs/steam-cef-system.md`: remove the 768 KiB sentence (BUILD-015, if B032 has not already).
  - `src/WSGM.Plugin.Sdk/README.md` "depends on nothing": rewritten by B052 (A02-F002). Do not touch it here.
  - Linked-file paths in `src/WSGM.PackagedLaunch/AGENTS.md` and `src/WSGM.DeviceLab/README.md` change in B173.
  - Every AGENTS.md edit is its own diff, shown with the batch and applied per D4, followed by `.\eng\check-agent-guidance.ps1`. CLAUDE.md files are symlinks: edit AGENTS.md only.
- **Tests:** `npm run format:check`; `.\eng\check-agent-guidance.ps1`.
- **Plan v2:** B176.
- **Related:** A02-F002 (B052), U01-068, PV08-011; BUILD-015, BUILD-019.

### BUILD-V-001: the asset builder writes into the embedded-resource folder even in check mode

- **Severity:** low (missed finding, verifier).
- **Where:** `eng/build-steam-assets.mjs` (`unformattedPath = join(assetDirectory, "NativeQamBootstrap.generated.js")`, `writeFile`, `run("node", [prettier, "--parser", "babel", unformattedPath])`, then `rm` in `finally`); `src/WSGM/WSGM.csproj` (`Core\SteamUiAssets\*.js` is an `EmbeddedResource` glob); the single-file check in the same script.
- **Problem:** every run, `--check` included, writes a second `.js` file into an embedded-resource folder. A `dotnet build` running at the same moment embeds the stray file. A killed node process leaves it behind, and the next gate fails with "Steam UI assets must stay an explicit, reviewed set".
- **Best solution:** give the `run` helper's `spawnSync` the input on stdin. Call `run("node", [prettierCli, "--stdin-filepath", outputPath], { input: compiled.slice(markerIndex + bundleMarker.length).trimStart() })`, with no `--parser`: Prettier infers babel from the `.js` path and applies the `.prettierrc.json` override (`endOfLine: lf`) for `src/WSGM/Core/SteamUiAssets/*.js`, exactly as before. Delete `unformattedPath`, the `writeFile`, and the `try`/`finally` `rm`. Check mode then writes nothing, and build mode writes only `NativeQamBootstrap.js` as before.
- **Tests:** `npm run steam-assets:build` must produce a byte-identical asset (no git diff on `NativeQamBootstrap.js`); `npm run steam-assets:check`.
- **Plan v2:** B032.
- **Related:** BUILD-015, BUILD-C-001.

### BUILD-V-002: plans tell workers to run the gate mid-implementation, but it only works on a committed head

- **Severity:** low (missed finding, verifier; a plan-procedure gap, not a script bug).
- **Where:** `eng/verify.ps1` (in-place `jb cleanupcode`, then a diff of all of `src` and `tests`); every batch validates in the shared working tree with uncommitted edits.
- **Problem:** a batch that runs `eng/verify.ps1` before committing fails on its own edits, or with `-Fix` mixes Rider's rewrites into the batch diff. Until B037 and B002 land, it also runs the unsafe suites (BUILD-001).
- **Best solution:** the procedure fix plan v2 already adopted, with no script change. Batches validate with `dotnet build <project>` (or `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true` for cross-project batches), their narrow `--filter` tests, and Rider cleanup plus `npm run format` on the files they changed. `eng/verify.ps1` runs only on a committed head, once, at B179. BUILD-002's hash comparison makes a later uncommitted run tolerable, but the rule stays.
- **Tests:** none.
- **Plan v2:** protocol section 3, steps 4 and 11; B179.
- **Related:** BUILD-001, BUILD-002.

### BUILD-V-005: the proposed tools build compiles WSGM again for a second runtime graph

- **Severity:** low (missed finding, verifier).
- **Where:** `tools/DeckSpike/DeckSpike.csproj` (`win-x64`, self-contained, references `src/WSGM/WSGM.csproj`); `tools/OverlayPreview/WSGM.OverlayPreview.csproj` (`win-x64`, self-contained, references the self-contained `WSGM.UiTests`); `tools/PerfLab` (Microsoft.Windows.EventTracing) and `tools/HcDeviceExtract` (Roslyn).
- **Problem:** building the tools in verify restores and compiles WSGM and its references again for the RID instead of reusing the solution build. That roughly doubles the gate's build time, and DeckSpike is the costliest.
- **Best solution:** D12 is decided: DeckSpike and SteamReceiver are deleted, which removes the second full WSGM graph. Then use exactly the shape in BUILD-003: one `dotnet restore` per remaining tool at verify's restore step, and `--no-restore` builds after the test loop with the solution build's global properties, so the shared project references are reused rather than built again. OverlayPreview's UiTests graph and PerfLab's EventTracing restore are accepted once per gate. Keep the tools out of the slnx (see BUILD-003).
- **Tests:** as BUILD-003.
- **Plan v2:** B174, decided: D12, delete DeckSpike and SteamReceiver.
- **Related:** BUILD-003, BUILD-017.

### BUILD-C-001: the asset builder's process helper keeps a hidden 1 MiB cap once the 768 KiB bound goes

- **Severity:** low (missed finding, solution check).
- **Where:** `eng/build-steam-assets.mjs` (`function run(command, args, options = {})`: `spawnSync(command, args, { cwd, encoding: "utf8", ...options })` with no `maxBuffer`; the Prettier call returns the whole formatted asset on stdout).
- **Problem:** `spawnSync` defaults `maxBuffer` to 1 MiB (1,048,576 bytes). The asset is 645,950 bytes today and grows with every surface. BUILD-015 removes the 768 KiB check, so the next limit is this default: once the formatted asset passes 1 MiB, Prettier's stdout overflows, `spawnSync` kills it with `ENOBUFS`, and `run` throws "node ... failed" with a partial dump of the asset. Checked locally: a 2 MiB stdout fails with `ENOBUFS` under the default and succeeds with `maxBuffer: Infinity`. That is an arbitrary cap left in place by accident.
- **Best solution:** add `maxBuffer: Number.POSITIVE_INFINITY` to the `spawnSync` options in `run` (before `...options`). Nothing else changes. It covers the `tsc` call too, which prints little.
- **Tests:** `npm run steam-assets:check`; `npm run steam-assets:build` leaves `NativeQamBootstrap.js` byte-identical.
- **Plan v2:** B032, with BUILD-015 and BUILD-V-001 (same file).
- **Related:** BUILD-015, BUILD-V-001; STEAMHOST-V-003, TOOLKITJS-037.

### BUILD-023: tests compile two copies of linked types

- **Severity:** nit (the verifier widened the scope).
- **Where:** `tests/WSGM.Tests/WSGM.Tests.csproj` (references both `src/WSGM` and `src/WSGM.Launch`); `src/WSGM.Launch/WSGM.Launch.csproj` (links `ScheduledTaskXml.cs`, `WindowsCommandLine.cs` and the steam-input-lease binding `bindings/SteamInterop.Net/*.cs`, whose types such as `SteamInputClientOptions` and `SteamInputStatus` are public); `src/WSGM/WSGM.csproj` (the same binding). PackagedLaunch is not involved: it grants no `InternalsVisibleTo`.
- **Problem:** any test that names one of these types directly fails with CS0433 (ambiguous type). That holds even for the public binding types, without `InternalsVisibleTo`. Today the ambiguity is avoided only by testing through `UnelevatedLauncher.BuildTaskXml`.
- **Best solution:** in `WSGM.Tests.csproj`, set `Aliases="launch"` on the `WSGM.Launch` `ProjectReference`. The shared types then resolve to WSGM's copy under the global alias. Add `extern alias launch;` and qualify Launch types with `launch::` (`using launch::WSGM.Launch;`, `launch::WSGM.Launch.Program`) in the two test files that use types from the Launch assembly: `tests/WSGM.Tests/Launch/LaunchWrapperTests.cs` and `tests/WSGM.Tests/Launch/SuspendedProcessTests.cs` (checked with `git grep -n "using WSGM.Launch\|WSGM\.Launch\.[A-Z]" -- tests`). The plan's "five files" is wrong: `UnelevatedLauncherTests` tests WSGM's own `UnelevatedLauncher`, and `LaunchWrapperCommandTests` and `RunningApplicationTargetTests` only contain the string `"WSGM.Launch.exe"`. If the compiler then reports CS0246 in another file, qualify that file too. Do this in the batch that moves the linked files, so the alias is decided once.
- **Tests:** `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Launch|FullyQualifiedName~UnelevatedLauncher|FullyQualifiedName~SuspendedProcess|FullyQualifiedName~RunningApplicationTarget"` (the build itself proves no other file needed the alias).
- **Plan v2:** B173.
- **Related:** BUILD-019.

### BUILD-024: mixed target frameworks without a stated rule

- **Severity:** nit.
- **Where:** `net10.0-windows`: the Plugin SDK, Device SDK, Install, Launch, LogonService, AMD, Intel and IR projects. `net10.0-windows10.0.19041.0`: WSGM, Setup, PackagedLaunch, Avalonia.LiveBackdrop, Ally, Claw, HC, NVIDIA and Device Lab.
- **Problem:** nothing tells a contributor which framework a new or changed project should use, so projects drift.
- **Best solution:** state the existing rule in one sentence in the root `AGENTS.md` "Where things are" section (its own diff, shown with the batch and applied per D4): "Projects target `net10.0-windows`; a project moves to `net10.0-windows10.0.19041.0` only when it uses WinRT or references WindowsDeviceControl." Change no project here. The Intel plugin changes framework only if a GPU batch puts it on WDC.
- **Tests:** `.\eng\check-agent-guidance.ps1`; `npm run format:check`.
- **Plan v2:** B176.
- **Related:** none.

### BUILD-032: stale build comments and small script defects

- **Severity:** nit.
- **Where and fix:**
  - `eng/wsgm-revision.targets`: "(2.0.0)" in the header comment. Drop the literal version ("stays the release version").
  - `.prettierignore` line 1: "C# is formatted by dotnet format". Change it to "C# is formatted by Rider's Full Cleanup (jb cleanupcode) and dotnet format".
  - `eng/verify.ps1`: the comment above `check-version-sync.ps1` mentions "the installer's fallback version", but there is no Inno installer and the script checks only `app.manifest`. Reword it to "The manifest identity is a hand-maintained copy of the csproj version."
  - `eng/check-agent-guidance.ps1` and `eng/assert-component-staging.ps1` open with `<#[`. Make it `<#`.
  - `eng/assert-component-staging.ps1`: `WSGM.DeviceHost.exe` (retired) in the unrelated-executable list. Delete it (B033 edits this file).
  - `eng/pack-device.ps1` dot-sources `plugin-package-common.ps1` twice, and the `try` body in `eng/publish-device-lab.ps1` is not indented. Fix both in B175, which rewrites both files. Do not patch code a later batch rewrites.
  - The `Directory.Build.props` `AVLN3001` comment (PV08-011) stays with its ledger owner.
- **Tests:** a PowerShell parse of the changed scripts; `npm run format:check`; `.\eng\check-agent-guidance.ps1`.
- **Plan v2:** B032 (first four items and the `<#[` in `check-agent-guidance.ps1`), B033 (`assert-component-staging.ps1`), B175 (the last two).
- **Related:** PV08-011, BUILD-030.

### BUILD-033: `update-ui-baselines.ps1` restates every case name

- **Severity:** nit.
- **Where:** `eng/update-ui-baselines.ps1` (`[ValidatePattern(...)]` holds a hand-maintained regex of all 51 baseline names).
- **Problem:** adding a UI test case means editing an unrelated script's regex, or the baseline cannot be promoted.
- **Best solution:** replace the pattern with `[ValidatePattern('^[a-z0-9][a-z0-9-]*$')]`, which keeps a case name a plain file name with no path characters. The script's existing loop already refuses any name without `TestResults/ui/<case>/actual.png`, and that is the real check.
- **Tests:** a PowerShell parse. `.\eng\update-ui-baselines.ps1 -Case ..\x` must be refused by validation. Do not promote a baseline as a test.
- **Plan v2:** B176.
- **Related:** none.

### BUILD-036: two repository-root locators

- **Severity:** nit.
- **Where:** `tests/Shared/RepositoryFiles.cs` (`Root`, walking up to `WSGM.slnx`); `src/WSGM.DeviceLab/Preflight/OutputPathPolicy.cs` (`DeviceLabRepositoryLocator`, `SolutionMarkers = ["WSGM.slnx"]`); its only test consumer is `tests/WSGM.DeviceLab.Tests/Eng/DevicePackageOutputTests.cs`.
- **Problem:** a test suite locates the checkout through product code. The two locators can drift.
- **Best solution:** no new code. The production locator stays, because Device Lab needs it at run time. B175 deletes `DevicePackageOutputTests.cs` (BUILD-028), its only test use, so tests use `RepositoryFiles.Root` exclusively. In B176, confirm with `git grep -n DeviceLabRepositoryLocator -- tests` that no test references it.
- **Tests:** the grep above returns nothing.
- **Plan v2:** B176 (after B175).
- **Related:** BUILD-025, BUILD-028.

### BUILD-V-003: the release workflow's version stamp trips the cleanup diff

- **Severity:** nit (missed finding, verifier).
- **Where:** `.github/workflows/release.yml` (runs `./eng/stamp-version.ps1 -Tag $env:REF_NAME`, which rewrites `src/WSGM/WSGM.csproj` and `src/WSGM/app.manifest`, then `./eng/verify.ps1`); `eng/verify.ps1` (`git diff --exit-code --stat -- src tests`).
- **Problem:** any tag whose version differs from the committed csproj (a `-rc` tag, or a tag pushed before the csproj bump) fails the release job at verify, on a csproj change jb never made.
- **Best solution:** fixed by BUILD-002. The jb check compares hashes of `*.cs` files before and after cleanup, so the stamped csproj and manifest no longer count. Change nothing in `release.yml`.
- **Tests:** covered by BUILD-002. Optionally, the maintainer runs `stamp-version.ps1 -Tag v2.1.0-rc1` and then verify on a scratch commit. Never tag or release for this.
- **Plan v2:** B032.
- **Related:** BUILD-002.

### BUILD-V-004: a stale agent worktree nested in the checkout pollutes repository-wide sweeps

- **Severity:** nit (missed finding, verifier).
- **Where:** `D:/Coding/WSGM/.claude/worktrees/agent-a1a3754e7bef61c5c` (commit 8fa7510e, still on the USBip 0.9.8.0 pins) plus twelve other registered worktrees (`git worktree list`). `.prettierignore` (`.claude/`) and the guidance check already exclude them.
- **Problem:** `rg --hidden --no-ignore` and glob sweeps return stale copies of every file, and this refactor relies on "enumerate every caller" sweeps. A worker could edit or count a stale copy.
- **Best solution:** procedure only. Every caller inventory and sweep excludes `.claude/` and `.codex/` (`rg ... -g '!.claude/**' -g '!.codex/**'`, or `git grep`, which sees only tracked files). `.codex/` is gitignored but `--no-ignore` reaches it, and the plan and review files there quote source text that a sweep would count as callers. Removing the stale worktrees is destructive and is the maintainer's call. No agent runs `git worktree remove` or `prune`.
- **Tests:** none.
- **Plan v2:** protocol section 3, step 2.
- **Related:** none.

## Refuted or no-change

The verifier refuted no finding outright. These ids need no change: plan v2 marks them no-change, the review itself recommended none, the solution check found the proposed change harmful or without effect (BUILD-027, BUILD-035), or the maintainer dropped it (BUILD-009).

- **BUILD-009** (low, `claude.yml` uses moving action tags `actions/checkout@v7` and `anthropics/claude-code-action@v1`): Dropped by maintainer decision (security theater, DECISIONS.md). The finding was only about a moved tag running unreviewed code; the workflow works as it is. B032 drops the item.
- **BUILD-020** (low, two rotating file logs): no merge. Pulling `Log`'s statics and its cross-process mutex into the dependency-light launchers would break their rule. `RotatingFileLog` only moves to `src/Shared/Process` with BUILD-019.
- **BUILD-022** (low, OverlayPreview references a test project): keep. The preview is UI-test infrastructure; BUILD-003's tool build compiles it.
- **BUILD-029** (nit, getter-style tripwires): keep `ContractBoundaryTests.TheApiVersionIsPinnedSoRaisingItIsADeliberateAct` as the regression guard the SDK guide asks for. The Device SDK dependency assertion lives in `DeviceBoundaryTests` (verifier correction) and folds into `ProjectGraphTests` (BUILD-021).
- **BUILD-029a** (nit, test-project boilerplate repeated 13 times): no change. A `tests/Directory.Build.props` would have to import the root props and adds an MSBuild layer for little gain. Two projects use xunit.v3 deliberately.
- **BUILD-027** (low, the SDK packages have two version identities; moved here by the solution check): no change, and B147 drops its "SDK feed version follows the csproj" item. The per-commit `-p:Version=$wsgmVersion` stamp in `eng/build-bundle.ps1` `Get-SdkFeed` is what keeps a stale SDK out of community builds: NuGet extracts a restored package into the global packages folder (`~/.nuget/packages`, which `ci.yml` also caches with `restore-keys` fallback) and never re-reads a source for an id and version it already holds. Packing at the fixed csproj version (0.4.0, 0.2.0) would let a community build compile against an SDK cached from an earlier commit whenever the SDK changed without a version bump, which is every commit of the Device API 12 work. The stamped identity has no runtime effect: `PluginLoadContext.HostOwned` hands a plugin the host's SDK assembly by simple name. No curated community package exists today (all six `plugins/curated/*.json` are first-party).
- **BUILD-035** (nit, `dev-deploy.ps1` writes its elevated swap script to `publish\dev-deploy-swap.ps1`; moved here by the solution check): no change, and B176 drops the item. The proposed `-EncodedCommand` closes nothing: the same elevated swap copies the user-writable `publish\App` binaries, named by the user-writable request JSON, into `%ProgramFiles%\WSGM\App`, and dev-deploy then restarts WSGM, which runs elevated. A same-user process that could swap the script file can swap `publish\App\WSGM.exe` or the request instead. This is an attended developer tool deploying the user's own build, and the extra code would buy no protection. The maintainer's decision confirms it: dropped (security theater, DECISIONS.md).
- **BUILD-031** (nit, tracked symlink `src/WSGM/ThirdParty/LoadingIndicators`): keep the symlink until a `Link`-based `AvaloniaResource` include is proven to keep the avares paths the XAML analyzer resolves. The ledger's "accepted" disposition (U04A-LFA-027) is unproven.
