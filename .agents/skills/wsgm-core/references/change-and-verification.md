# Change and verification

## Trace the change before editing

Write down the smallest state flow:

```text
event / user intent / observation
  -> one policy decision
  -> one owning manager or store
  -> side effect through a narrow adapter
  -> observed/read-back state
  -> Settings, Overlay, Steam/QAM, or log projection
  -> ordered cleanup/restoration
```

Search current code/tests for every producer and consumer of the state. Reuse an existing manager,
coordinator, command lane, projection, enum, or disposal point before adding a parallel path. A UI
control is rarely the state owner; a native adapter is never the policy owner.

For bugs, locate the first boundary contradicted by evidence: input/observation, policy, ownership,
dispatch, side effect, readback, projection, or teardown. Do not patch the last visible symptom when
the owner stopped publishing or disposed early.

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

Typical command from the WSGM root:

```powershell
dotnet test tests/WSGM.Tests/WSGM.Tests.csproj --configuration Release --filter "FullyQualifiedName~Area"
```

For reload, overlay publication, tray/Explorer, and shutdown ownership, start with these concrete
files:

- `tests/WSGM.Tests/ConfigurationTests.cs`
- `tests/WSGM.Tests/QuickAccessSheetTests.cs`
- `tests/WSGM.Tests/SessionModesTests.cs`
- `tests/WSGM.Tests/ApplicationShutdownTests.cs`
- `tests/WSGM.Tests/TrayProtocolTests.cs`
- `tests/WSGM.Tests/ExplorerReadinessTests.cs`
- `tests/WSGM.Tests/ExplorerShellPolicyTests.cs`

Current gaps need dedicated regression tests: repeated watcher initialization, stale reload after
dispose, dispatcher-only publication to the current view model, duplicate game-mode entry retaining
the live tray owner, every shutdown phase continuing after injected faults, tray-before-Explorer
ordering, anchor retention on failed recovery, repeated shutdown, and `SessionEnd` never launching
Explorer. Existing failure aggregation alone does not prove phase continuation.

Test policy and state machines with fakes/temp roots. A compile-only build with
`SkipNativeArtifacts=true` does not prove the package or installer contains native payloads.

## Submodules and generated assets

Before changing reusable code, inspect the child repository's status and guidance. Commit/test/push
the child first, then stage only its gitlink in WSGM. For the nested device graph the order is SDK
-> Device Lab -> device package -> WSGM. Never run an update command that overwrites a moved or
dirty child checkout.

Steam injected source is generated into `src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js`; use the
Steam CEF skill and `npm run steam-assets:build`, never edit the bundle/hash by hand.

`eng/build-viiper.ps1 -Validate` force-checks out, resets, and cleans its `SourceRoot`. Use it only
on a disposable VIIPER source tree with no work to preserve.

## Repository gate and delivery

Follow the root AGENTS.md validation policy: run the gate for initial delivery, then use focused
checks for follow-ups unless the changes justify another full run:

```powershell
./eng/verify.ps1
```

The gate checks formatting, generated Steam assets/claims, guidance links, PowerShell syntax,
live-data exclusions, pins, Steam Input, restore, warning-clean Release builds, tests, and coverage.
It does not prove live shell, Steam, device, controller, or hardware behavior.

`./eng/verify.ps1 -Fix` writes formatting changes. Use it only when the resulting tree is within the
task and every change will be reviewed. `./build.ps1` stages native components, all applications,
device/controller payloads, and the Inno installer; run it only for an explicitly requested release
or installer handoff.

Before commit/push:

1. inspect staged names and diff; never stage unrelated files;
2. reconcile docs, plans, scoped guidance, and skill instructions;
3. confirm each changed child commit is published before its parent pin;
4. commit the intended scope, push the current branch, and verify clean local/upstream equality;
5. state which attended/live acceptance remains, rather than treating the automated gate as hardware
   proof.
