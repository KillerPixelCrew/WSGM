# Change and verification

## Trace the change before editing

Write down the smallest state flow:

```text
event / user intent / observation
  -> one policy decision
  -> one owning manager or store
  -> side effect through a narrow adapter
  -> accepted/observed state, or explicit uncertainty
  -> Settings, Overlay, Steam/QAM, or log projection
  -> ordered cleanup/restoration
```

Search current code/tests for every producer and consumer of the state. Reuse an existing manager,
coordinator, command lane, projection, enum, or disposal point before adding a parallel path. A UI
control is rarely the state owner; a native adapter is never the policy owner.

For bugs, locate the first boundary contradicted by evidence: input/observation, policy, ownership,
dispatch, side effect, observation, projection, or teardown. Do not patch the last visible symptom
when the owner stopped publishing or disposed early.

An accepted Windows/device write is published according to that API's contract. Do not add a
mandatory readback or disable a control because a separate getter cannot confirm the value.
Uncertain writes are not retried automatically; Explorer and ownership-ledger recovery still require
their explicit verification evidence.

## Implementation checklist

- Preserve unrelated worktree and submodule changes.
- Keep nullable analysis, code-style warnings, public XML docs, cancellation, and disposal clean.
- Update state atomically and publish one coherent snapshot when consumers require consistency.
- Make repeated enable/disable/reload/stop safe; cover failure after each acquisition step.
- Validate external paths/data under bounds and reject reparse/traversal/shape changes where the
  existing boundary does.
- Keep UI strings/localization and presentation in the owning UI layer; keep native ABI in Interop.
- Update the mechanism doc, decision/finding if behavior changed, tracker when scope/status changed,
  and affected skill/guidance in the same commit.
- Generated outputs are never hand-edited. Use the owning generator and check source, hash, and
  ownership tests.
- Before live Steam CEF tool attachment, confirm Steam and Big Picture have fully started from
  Steam's file logs. Do not use a listening endpoint, a window or a first connection as the startup
  check. Route the operation through the specialized Steam CEF skill; early attachment can hang the
  whole Steam UI and require force-closing Steam.

## Focused tests

Choose a filter matching the actual boundary, for example:

| Area                        | Useful test-name filters                                                  |
| --------------------------- | ------------------------------------------------------------------------- |
| Program/modes/boot/recovery | `ModeSelection`, `Boot`, `Explorer`, `ShellAnchor`, `ApplicationShutdown` |
| Config/settings saves       | `Configuration`, `SettingsSaveMerge`, `BootManifest`, `SplashAssets`      |
| Shell transitions/tray      | `SessionModes`, `ExplorerReadiness`, `TrayProtocol`, `UpdateExitWatcher`  |
| Overlay/input/navigation    | `Overlay`, `QuickAccess`, `Input`, `Gamepad`, `Curve`                     |
| RTSS/per-app/AutoTDP        | `Rtss`, `Performance`, `RunningApplication`, `AutoTdp`, `FrameLimit`      |
| Display/power/radios        | `Display`, `RefreshRate`, `KeepAwake`, `Radio`, `Audio`                   |
| SD cards                    | `SdFormat`, `RemovableDrive`, `CardLibrary`                               |
| Device host                 | use `wsgm-device-sdk` and its host test map                               |
| Steam CEF                   | use the Steam CEF skills and toolkit tests                                |

After the maintainer's manual test report, unless tests were explicitly requested sooner, use a
filter for the actual boundary from the WSGM root:

```powershell
dotnet test tests/WSGM.Tests/WSGM.Tests.csproj --configuration Release --filter "FullyQualifiedName~Area"
```

For reload, overlay publication, tray/Explorer, and shutdown ownership, start with these concrete
files:

- `tests/WSGM.Tests/Core/Config/ConfigurationTests.cs`
- `tests/WSGM.Tests/App/ModeSelectionTests.cs`
- `tests/WSGM.Tests/Overlay/QuickAccessSheetTests.cs`
- `tests/WSGM.Tests/Shell/Session/SessionModesTests.cs`
- `tests/WSGM.Tests/Core/Session/ApplicationShutdownTests.cs`
- `tests/WSGM.Tests/Shell/Session/SessionShutdownTests.cs`
- `tests/WSGM.Tests/Core/Desktop/TrayProtocolTests.cs`
- `tests/WSGM.Tests/Shell/Desktop/ExplorerReadinessTests.cs`
- `tests/WSGM.Tests/Core/Desktop/ExplorerShellPolicyTests.cs`

Use the existing tests to identify what a behavioral change actually leaves uncovered. Relevant
cases include repeated watcher initialization, stale reload after dispose, dispatcher-only
publication to the current view model, duplicate entry retaining the live tray owner, phase
continuation after faults, tray-before-Explorer ordering, anchor retention on failed recovery,
repeated shutdown and `SessionEnd` suppressing Explorer. `SessionShutdownTests` already covers the
admission-step failure seam and deadline joins; `ApplicationShutdownTests` covers reason priority,
single ownership and deadline tightening. Do not present a historical gap list as current coverage
without reading the tests. Documentation-only edits need no new behavioral tests.

Test policy and state machines with fakes/temp roots. A compile-only build with
`SkipNativeArtifacts=true` does not prove the package or installer contains native payloads.

## Submodules and generated assets

Before changing reusable code, inspect the child repository's status and guidance. Commit/test/push
the child first, then stage only its gitlink in WSGM. LibHandheld owns handheld contracts and
protocols; WSGM owns its host adapters. Update both contract projections and all affected consumers
together when that boundary changes. Use the repository's direct default-branch workflow unless the
user specifically requested a pull request. Never run an update command that overwrites a moved or
dirty child checkout.

Steam injected source is generated into `src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js`; use the
Steam CEF skill and `npm run steam-assets:build`, never edit the bundle/hash by hand.

`eng/build-viiper.ps1 -Validate` tests and builds the `external/viiper` submodule as checked out.

## Repository gate and delivery

Follow the root AGENTS.md validation budget. Build and perform an authorized development deployment
first; tests and coverage wait for the maintainer's manual report unless requested sooner. Choose
one useful batch for the diff and reuse successful proof while its inputs are unchanged. Do not run
`eng/verify.ps1` automatically for an initial feature, follow-up, PR or push. A full gate needs an
explicit request or a concrete shared-validation change that requires it; name the reason first. A
narrow guidance-link checker change can be validated by its own positive and negative cases.

The full gate owns formatting, compilation, tests and coverage when requested. It does not prove
live shell, Steam, physical controllers or hardware behavior. Every feature's closing batch also
checks Stable/Beta Steam fingerprints and asset claims as AGENTS.md requires, plus patch health
after an authorized deployment. Instruction-only edits require no application build or full gate.

`./eng/verify.ps1 -Fix` writes formatting changes. Use it only when the resulting tree is within the
task and every change will be reviewed. `./build.ps1` stages native components, all applications,
the plugin bundle and controller payload, and the WSGM setup; run it only for an explicitly
requested release or setup handoff.

`build.ps1` itself runs Steam Input/VIIPER test-bearing validation, so it follows the same
manual-first timing. It is not a compile-only substitute for `verify.ps1`. `docs/development.md`
gives the separate managed/native compilation commands, generated-artifact ownership and required
toolchains.

Before commit/push:

1. inspect staged names and diff; never stage unrelated files;
2. reconcile docs, plans, scoped guidance, and skill instructions;
3. confirm each changed child commit is published before its parent pin;
4. commit the intended scope directly to `master` (or `wsgm` in external/viiper), with no task
   branch or pull request unless the task asks for one;
5. validate under the root AGENTS.md budget; guidance changes take targeted Prettier checks and
   `eng/check-agent-guidance.ps1`. Do not duplicate passed checks for a committed-head result;
6. push, and verify clean local/upstream equality without polling CI in a loop;
7. state which tests were deferred and which attended/live acceptance remains, rather than treating
   the automated gate as hardware proof.
