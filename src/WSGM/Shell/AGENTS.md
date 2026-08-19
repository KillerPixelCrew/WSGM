# Shell

Shell coordinates the Explorer-first boot takeover, Steam lifecycle, desktop/game-mode transitions,
startup applications, tray host, removable storage, radio, audio, and system status.

- Shell transitions must fail open to desktop mode. Do not leave a half-transition with Explorer gone
  and Steam/overlay ownership unresolved.
- Explorer must finish logon preparation before a takeover; Steam starts only after Explorer exits.
  Do not change the orderly Exit Explorer mechanism or kill Winlogon replacement processes.
- The boot splash's Switch to desktop is a recovery path: cancel the service takeover before the
  orderly exit when possible; if that request already began, skip game-mode setup and restart
  Explorer through the normal desktop transition. Never drop it at the transition-serialization gate.
- Keep blocking Explorer work off the UI thread and serialize mode transitions through `SessionModes`.
- `TrayHost` never coexists with Explorer's tray, and elevated WSGM must retain its `WM_COPYDATA` UIPI
  allowance for unelevated applications.
- Radio, audio, and storage managers reconcile state in place. Preserve user focus and surface
  device-facing failures through logs and retryable UI states.
- Card-reader paths are reusable identities: discover physical removable/hot-pluggable devices and
  key registration, removal, and retirement by `contentId`, never by drive letter alone.
- Steam VDF mutations are shape-checked, renumbered, backed up once, and atomically replaced; a
  random write-through diskpart script and post-failure compensation protect destructive formats.
- **The SD format is THREE diskpart runs, not one** (`SdFormatManager`, device-observed 2026-08-16):
  clean + `create partition primary`, then a wait for the new partition's volume interface
  (`NativeStorage.ListVolumeInterfaces` mapped back to the disk number, 20 s cap), then
  `select partition 1` + `format` (3 attempts), then `assign` only if automount did not already
  put the card on its own letter. In one script, `format` straight after `create partition`
  fails with "no volume selected" (exit `E_INVALIDARG`) whenever the volume manager surfaces the
  volume slower than diskpart moves on — a 512 GB card in the Claw's Realtek reader lost that race
  every time while a 256 GB card won it. Do not merge the scripts back together, and keep the
  `Format: volume on disk N appeared after … ms` / `no volume appeared` lines — they are how the
  timing is diagnosed from a pasted log.
