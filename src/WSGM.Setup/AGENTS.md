# WSGM setup

This scope owns `WSGM.Setup.exe`: install composition, hardware-driven plugin and component
selection, privilege boundaries, updates, repair, rollback and uninstall. Read docs/boot-and-shell.md,
docs/elevation.md, and the relevant device or input document before changing behavior. The approved
screen design is `docs/mockup.html`; keep flow, wording and layout aligned with it.

## Invariants

- Setup is one elevated process. The product lives under `%ProgramFiles%\WSGM` (`App`, `Plugins`,
  `Setup`); per-user state stays in `%LOCALAPPDATA%\WSGM`, and setup's own machine records
  (`components.json`, `bundle.json`, `setup.log`) in `%ProgramData%\WSGM`. Keep that boundary
  explicit. Setup starts WSGM itself only after the install, the way the Inno installer did.
- The WSGM csproj Version is the only version source. WSGM.Setup.csproj reads it; do not introduce
  another copy.
- Preserve the supported Windows and architecture checks and the Steam prerequisite. Fail with an
  actionable message before modifying the machine.
- Hardware detection decides the device plugin: only a plugin whose hardware rules match is
  installed, never two. The installed plugin's declared capabilities decide the components
  (`SetupComponents`): VIIPER, USB/IP and HidHide only for a controller role. No plugin means plain
  WSGM with device integration off.
- Setup asks every first-run choice once, through setup answers that WSGM exports and applies
  (`--export-setup-answers`, `--setup --answers`). Never let a repair, an update or a quiet install
  rewrite a start mode or an integration switch the user has already chosen. Apply the Steam
  autostart takeover only on explicit consent, never under `/quiet` on a fresh install.
- Stop the service before replacing runtime files. Stage `App` beside the installed one and swap it
  through `App.previous`, so an interrupted or failed install rolls back to a startable WSGM.
- The exit events, shell mutex, device-owner marker and anchor recovery event are a cross-version
  contract with the running WSGM. Keep their names, access rights, waits and order; the setup
  contract tests pin them.
- Setup asks running WSGM to perform its bounded Steam and launch-wrapper pre-stop. Steam that is
  still running gets the same graceful `steam://exit` from setup, never a termination; setup never
  terminates Steam or a wrapper, and refuses replacement while either still owns a live game tree.
  Force stops are limited to WSGM's own images in setup's session.
- Steam Input shim cleanup must ask the runtime ownership logic to reconcile it. Never delete or
  replace a Steam DLL merely because its filename matches.
- Restart-required state is reserved for the USB/IP step and genuine operating system requirements.
  Quiet runs must not invent an interactive restart.
- Uninstall removes only WSGM-owned files and restores shell, service, driver and input state in a
  recoverable order. The HidHide cleanup always runs, even when HidHide stays installed; an
  unverified cleanup is reported with the device paths and keeps the ownership ledger. A driver that
  was present before WSGM is never offered for removal.
- A WSGM 1.0 install is removed through its own Inno uninstaller before 2.0 installs. Setup never
  installs on top of it and never carries its files over.

Validate setup work with the setup tests, the full gate under the root validation policy, and
build.ps1 when a real setup artifact is required. Exercise install, update, repair, rollback and
uninstall paths for any ownership or sequencing change; those are live-machine actions and need the
maintainer's direction.
