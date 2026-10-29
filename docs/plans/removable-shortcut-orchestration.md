# Issue #200 orchestration

This is the delivery split for the approved
[removable-shortcut plan](removable-shortcut-availability.md). The implementation uses real
supervised Orca workers in the existing task checkout. No new checkout, branch, worktree or pull
request is part of this run.

Run: `run_019fca0b05c7`, started 2026-10-29 Europe/Berlin.

| Task                                                    | Agent  | Writable ownership                                                                           | Dependencies               | Current evidence                                     |
| ------------------------------------------------------- | ------ | -------------------------------------------------------------------------------------------- | -------------------------- | ---------------------------------------------------- |
| `task_594ba20b4da8`: card manifest and shared contracts | Codex  | Shared managed-content model; new storage manifest/model/source files; managed launch helper | None                       | Source delivered; worker settled and released        |
| `task_cfebf3c6ab0d`: device index and account mappings  | Codex  | Import state store, import plan and mapping records                                          | None                       | Source delivered; worker settled and released        |
| `task_5845bc04a5fa`: reusable Steam availability        | Claude | SteamUiToolkit library badge/availability surface, owning TypeScript and documentation       | None                       | Source delivered; worker settled and released        |
| `task_c49f9fa3c7dc`: both Game Library UI surfaces      | Claude | Overlay library views/rows and WSGM library-import TypeScript                                | Agreed DTO/action contract | Source delivered; worker settled and released        |
| `task_f1b4d1743666`: runtime/import/reconciliation      | Codex  | GameLibraryService partials, managed shortcut monitor and source adapters                    | Manifest and index tasks   | Source delivered; worker settled and released        |
| `task_980c698dd046`: named render preparation           | Claude | Existing UI fakes, preview exporter and OverlayPreview entry point                           | Library UI task            | Named renders delivered; worker settled and released |

The coordinator owns cross-task API decisions, backend DTOs and bridge vocabulary, ShellSession
account observations and writer serialization, emulator writer account receipts, toolkit parent
switches, integration fixes, source documentation, shared formatting, asset generation, builds,
renders, commit/push and any authorized development deployment. Workers do not run competing shared
builds or generators.

## Agreed integration contracts

- The card owns portable inventory; `ImportStateStore` owns the device's cached libraries, shared
  per-title choices and per-account Steam mappings.
- `SteamShortcutSnapshot` carries the account observed across a complete shortcut read. All
  subsequent mapping saves/removals carry that captured account explicitly.
- Runtime and emulator shortcut writers share the host's serialized Steam operation boundary.
  Account changes or unknown ownership stop mutation.
- `StorageLibraryRecord` identifies the accepted local volume binding and manifest checkpoint. The
  helper reads the accepted local index only; it does not parse or adopt card manifests.
- `GameLibraryStorage` projects readable status, known title count, repair/binding availability and
  staged intent. `StageStorageActionAsync` stages `repair`, `acceptBinding` or cancellation; the
  existing Apply operation commits it.
- The decorative badge preference is separate from managed shortcut availability. Toolkit
  availability remains enabled under the Steam integration master while managed shortcuts exist.
- Confirmed missing content is pruned only on the correct readable volume. Disconnected storage,
  dependency failures, read errors and uncertain writes retain the appropriate cached inventory or
  cleanup receipt. Moved roots count as deleted content and require reimport, as directed by the
  maintainer.

## Validation ownership and limits

The coordinator prepares one coherent Release build and affected asset/render checks after source
integration. Automated suites and coverage wait for the maintainer's manual report. The full
verification gate, solution-wide cleanup and CI polling are not part of this task.

Seven ROM samples were copied from `Z:\ROMS` into the confirmed SD card's dedicated
`WSGM-Availability-Test\ROMS` tree, totalling 425,355,516 bytes. Originals and the existing
`SteamLibrary` were preserved. The exact volume identity and copied paths are recorded locally in
`TestResults/issue-200/media-copies.json`; this is preparation, not hardware or launch acceptance.

Both initial Claude launches failed before task injection because the terminal PATH lacked its
installed executable. A small user-local `claude.cmd` launcher made the existing executable
discoverable; `claude --version` succeeded. Orca recorded and released the failed attempts, and
explicit retries started the Claude workers.

The [delivery record](../../reports/issue-200-delivery.md) records per-task outcomes, actual checks,
installed artifact identities and remaining acceptance. All six tasks delivered source, all workers
were released, and the final coordinator inbox and reclaimable-worker list were empty. Live card
unmount/remount retained eight AppIds and disabled/restored native Play without restarting Steam.
