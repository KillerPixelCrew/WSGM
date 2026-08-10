# Shell

Shell coordinates the Explorer-first boot takeover, Steam lifecycle, desktop/game-mode transitions,
startup applications, tray host, removable storage, radio, audio, and system status.

- Shell transitions must fail open to desktop mode. Do not leave a half-transition with Explorer gone
  and Steam/overlay ownership unresolved.
- Explorer must finish logon preparation before a takeover; Steam starts only after Explorer exits.
  Do not change the orderly Exit Explorer mechanism or kill Winlogon replacement processes.
- Keep blocking Explorer work off the UI thread and serialize mode transitions through `SessionModes`.
- `TrayHost` never coexists with Explorer's tray, and elevated WSGM must retain its `WM_COPYDATA` UIPI
  allowance for unelevated applications.
- Radio, audio, and storage managers reconcile state in place. Preserve user focus and surface
  device-facing failures through logs and retryable UI states.
- Card-reader paths are reusable identities: discover physical removable/hot-pluggable devices and
  key registration, removal, and retirement by `contentId`, never by drive letter alone.
- Steam VDF mutations are shape-checked, renumbered, backed up once, and atomically replaced; a
  random write-through diskpart script and post-failure compensation protect destructive formats.
