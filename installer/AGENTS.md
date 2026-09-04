# Installer

This scope owns installer composition, component selection, privilege boundaries, upgrades,
rollback, and uninstall. Read docs/boot-and-shell.md, docs/elevation.md, and the relevant device or
input document before changing behavior.

## Invariants

- The application is per-user while the logon service, drivers, and other machine-wide components
  require elevation. Keep that boundary explicit.
- The csproj Version is supplied by build.ps1 through AppVersion. Keep the direct-ISCC fallback
  synchronized, but do not introduce another version source.
- Preserve the supported Windows and architecture checks and the Steam prerequisite. Fail with an
  actionable message before modifying the machine.
- Component choices must remain coherent across core, device integration, Device Lab, controller
  support, USB/IP, and HidHide tasks. A device package is installed only when the device-integration
  owner and package gate agree.
- Stop the service before replacing runtime files. Stage packages atomically and leave enough state
  for repair or rollback after an interrupted upgrade.
- Setup asks running WSGM to perform its bounded Steam and launch-wrapper pre-stop, but setup itself
  never terminates Steam or a wrapper. Refuse or defer replacement while either still owns a live
  game tree.
- Steam Input shim cleanup must ask the runtime ownership logic to reconcile it. Never delete or
  replace a Steam DLL merely because its filename matches.
- Restart-required state is reserved for the USB/IP task and genuine operating system requirements.
  Silent installs must not invent an interactive restart.
- Uninstall removes only WSGM-owned files and restores shell, service, driver, and input state in a
  recoverable order.

Validate installer work with focused tests, eng/verify.ps1, and build.ps1 when a real setup artifact
is required. Exercise install, upgrade, repair, rollback, and uninstall paths for any ownership or
sequencing change.
