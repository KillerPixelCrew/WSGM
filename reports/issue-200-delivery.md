# Issue #200 implementation and attended validation

Implemented on `NightHammer1000/removable-storage-nonsteam-shortcuts`, using the
[approved plan](../docs/plans/removable-shortcut-availability.md) and six supervised Claude/Codex
tasks in Orca run `run_019fca0b05c7`. The coordinator integrated the source, built, deployed and
performed the checks below on 2026-10-29. This record distinguishes those checks from remaining
acceptance; it is not a full regression pass.

## Delivered behavior

- A portable `.wsgm/library.json` owns each card's inventory. The device's schema-v2
  `library-import.json` caches known libraries and titles, with separate Steam-account mappings.
  Library UUID and volume GUID identify storage; drive letters are mount points.
- Reconciliation checks actual backing files. Disconnected or unreadable storage preserves titles.
  Confirmed missing content on the correct readable card is pruned, including the exactly owned
  shortcut. Running or unknown-running titles defer cleanup; uncertain native writes are not
  retried.
- Startup, resume, source changes, native volume events and changes in the existing drive inventory
  trigger checks. Software unmounts are detected through the existing inventory monitor. The bounded
  recovery check remains available without scanning every file on every drive inventory tick.
- Both Overlay and Steam Game Library expose storage status, title details, Recheck and staged
  repair/binding through Apply. New card inventory is not silently adopted or executed.
- Toolkit availability presentation greys unavailable shortcuts, provides the storage reason and a
  separate Recheck action, disables native Game Detail Play, and projects installed-only filtering.
  Running titles retain Stop. The launch helper independently checks the accepted local recipe. No
  persistent Valve shortcut-uninstalled flag is claimed or written.
- Shortcut-folder imports resolve `.lnk` target, arguments and working directory, including a folder
  on the device whose executables live on the card. No locate-moved-content workflow was added.

## Task outcomes

| Task                           | Agent  | Outcome and evidence                                                                                                                                                                                          |
| ------------------------------ | ------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Card manifest/shared contracts | Codex  | Delivered bounded manifest I/O, portable identity, path validation and helper changes; included in Release builds and real-card imports.                                                                      |
| Device index/account mappings  | Codex  | Delivered v2 migration, canonical inventory and account receipts; live import, prune and remount retained expected identities. Multi-account switching remains untested live.                                 |
| Reusable Steam availability    | Claude | Delivered toolkit surface and scoped CEF hooks; coordinator completed disabled Play and running-Stop guard. Disabled/enabled Play was observed in Steam. Installed filtering remains pending live acceptance. |
| Both Game Library UI surfaces  | Claude | Delivered Overlay and Steam storage flows; rendered Overlay cases and used Extensions QAM import flows in Steam. Coordinator fixed duplicate modal heading and unavailable counts.                            |
| Runtime/import/reconciliation  | Codex  | Delivered storage reconciliation and cleanup integration; coordinator fixed fresh running-state observation and software-unmount detection after live failures. Real-card prune and hotplug checks passed.    |
| Named render preparation       | Claude | Delivered six named cases, rendered at 980 x 640 and 1280 x 800 and personally inspected. These are render checks, not physical input acceptance.                                                             |

All expected dispatches settled, the coordinator inbox was empty, and no worker terminal owed a
cleanup decision at completion. Eight terminals, including two failed launches before task
injection, were released. Details are in the
[orchestration record](../docs/plans/removable-shortcut-orchestration.md).

## Build and deployment proof

- SteamUiToolkit implementation commits: `557f313e552f2d61479024f45281e7b3bc318b4f` and
  `a3f6a825fd5ee8129abf9d63fcd666a78b0676f5`, pushed before the parent gitlink was recorded.
- Release compilation of the OverlayPreview graph, including WSGM, setup and UI fixture assembly:
  zero warnings and errors. The managed launch helper also built successfully in Release.
- Steam Input Lease, VIIPER and UWP bridge native staging builds passed. No VIIPER source changed.
- Toolkit standalone prelude compilation and parent generated-asset drift check passed. The emitted
  WSGM asset SHA-256 is `570C264F3D9C238F1FB34CE96F21EF76588C2E62A4F4F5E9EA2C0215F367BEDD`.
- Twelve named Overlay screenshots were rendered and reviewed. Apply and Back remained reachable at
  the compact floor; lists intentionally scroll. No visual baselines were promoted.
- Authorized deployment used `eng/dev-deploy.ps1`. Installed development version: `2.4.2.1818`.
  Final restart used the same published files with `-SkipBuild`; no release version bump or setup
  release was made.
- Installed `WSGM.dll` SHA-256: `7744E9FCC9A985DE17A4099F1ABC7B8B1C5ABB1D9CA698A8D374CF4AB5937C03`.
  Installed `WSGM.PackagedLaunch.exe` SHA-256:
  `EE5938FC50F06ABD953173AB78AB761EBAE8385266C3BFEB76C5A89948E54BF3`.

Raw logs and captures are local ignored artifacts under `TestResults/issue-200/`, including
`release-build-final.log`, `helper-build.log`, `native-*.log`, `toolkit-assets-final.log`,
`wsgm-assets-final.log`, `dev-deploy-topology.log`, `dev-restart-final.log`, `ui-980/` and
`ui-1280/`. The new availability source fingerprints matched uniquely in the current library
entrypoint corpus. Eight unrelated existing fingerprints did not match that offline corpus, so this
was not a complete fingerprint compatibility pass.

## Attended checks on the inserted card

Seven ROM samples, 425,355,516 bytes, were copied from `Z:\ROMS` into
`D:\WSGM-Availability-Test\ROMS`. Original ROMs and the existing `SteamLibrary` were preserved. The
test library is `SDCard12`, UUID `8f3d17fa-ae3a-4de2-ab50-661116a2aa49`, bound to volume
`\\?\Volume{1bde88e7-d29a-11f1-8473-84d1c1f3b6a4}\`.

1. Imported the ROM folder through the **Extensions QAM tab**, using preview and Apply. The missing
   emulators correctly made ROMs unavailable without treating their files as deleted.
2. Moved one disposable copied GB ROM out of its recorded path. Reconciliation removed it from both
   ledgers and cleared the device shortcut receipt. Restored the copied file and explicitly
   reimported it through Library Manager. No original file on `Z:` was modified.
3. Imported a real Windows shortcut from a device-side folder, targeting a small portable executable
   on the card. Verified the adopted executable, distinct working directory and arguments
   `--source-shortcut --availability-check`. Native AppId: `2402465863`.
4. Unmounted the card with `mountvol D:\ /p`, with a timed remount safeguard. The software-unmount
   defect found in the first attempt was fixed and deployed. In the final clean Steam session, the
   automatic check retained all eight titles and the same eight AppIds, set the portable title to
   StorageUnavailable, and removed no inventory. No manual Recheck was needed.
5. On that title's native Game Detail page, the accessibility tree reported Play as disabled and the
   screenshot showed the disabled button plus `SDCard12: Unavailable`. Recheck stayed separate.
6. Remounted the same volume. In the same Steam session, without restart or manual Recheck, the
   title returned to Available, Play was enabled, the label returned to `SDCard12`, and all eight
   AppIds were unchanged. The card was left mounted and Steam responsive.

Final evidence: `unmounted-clean-result.json`, `remounted-clean-result.json`,
`live/detail-unmounted-clean.{txt,png}` and `live/detail-remounted-clean.{txt,png}`. Both
screenshots were personally inspected. Other records: `media-copies.json`, `move-check-result.json`
and `portable-import-result.json`.

## CEF debugging incident

MCP debugger attachment paused a Steam popup and froze Steam during validation. Steam was restarted
under the maintainer's explicit direction through the deployment script. The project MCP launcher
now prevents Puppeteer auto-attachment from requesting debugger startup pauses on Steam targets. The
final check stopped a stale MCP server, restarted Steam, confirmed current-run Steam/Big Picture
initialization in Steam logs and loopback ownership, then used the same MCP backend through a local
stdio client. The clean unmount/remount screenshots contain no debugger-pause banner. The client was
disconnected after capture; Steam was left running.

An earlier temporary dependency workaround in the separate `C:\WSGM\node_modules` package is
untracked environment state; the maintained fix is `eng/steam-cef-mcp.mjs` in this task checkout.
Already running clients must reconnect to load the updated launcher. Neither safeguard permits
connecting before the documented startup preflight.

## Remaining acceptance

Automated suites and coverage are deferred until the maintainer's manual report, per the repository
validation budget. No full `eng/verify.ps1`, CI polling or solution-wide cleanup was run. Physical
pull/reinsert, a changed drive letter, cross-device/account switching, duplicate UUIDs,
damaged/read-only manifests, running-title Stop behavior, large libraries, native installed-only
filtering/Big Art Mode, and controller/touch navigation still need attended regression acceptance.
The ROMs were not launched because the selected emulators are absent. The portable executable's
shortcut metadata and availability transitions were checked; a game launch was not part of this
pass.
